using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 聊天文本的清洗、传输混淆与内容策略。对应旧版 toolkit.py 的
/// <c>sanitize_text</c> / <c>chat_encrypt</c> / <c>chat_decrypt</c> / <c>mask_ip</c> / <c>chat_policy</c>。
///
/// <para>
/// <b>互通性：</b>加解密算法逐字节对齐旧版（SHA-256 派生 32 字节密钥流循环异或 + 4 字节校验标签 +
/// urlsafe Base64），因此新老客户端互相能读懂对方的聊天密文。改动这里的算法会直接破坏互通。
/// </para>
/// </summary>
public static class ChatCrypt
{
    /// <summary>聊天文本长度上限（旧版各处都用 500，见 chat.py:170 / hall.py:86）。</summary>
    public const int MaxTextLength = 500;

    private const int SaltLength = 8;
    private const int TagLength = 4;

    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", RegexOptions.Compiled);

    private static readonly Regex NormalizeDrops = new(
        @"[\u200b\u200c\u200d\u2060\ufeff\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", RegexOptions.Compiled);

    private static readonly Regex NonWord = new(@"[^\w]", RegexOptions.Compiled);

    /// <summary>网址检测：协议/www 前缀 / 裸域名 / IPv4（可带端口）。</summary>
    private static readonly Regex UrlPattern = new(
        @"(?:https?|ftp)://\S+"
        + @"|www\.\S+"
        + @"|\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?"
        + @"|(?<![a-zA-Z0-9])(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}(?:/[^\s]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>URL 分隔符伪装还原：[.] / (.) / （.） / 点 / · / 。 都还原为 .</summary>
    private static readonly Regex UrlMaskPattern = new(@"[\[(（]\s*\.\s*[\])）]|\.{2,}|[点·。．]", RegexOptions.Compiled);

    /// <summary>
    /// 违禁词表（违法/有害内容）。检测时先把文本压缩成「字母数字 + 汉字」再匹配，
    /// 因此词条无需逐一列举空格/符号变体；常见繁体词也已内置。
    /// </summary>
    private static readonly string[] BannedWords =
    [
        // 赌博/博彩
        "赌博", "博彩", "六合彩", "时时彩", "赌球", "赌场", "百家乐", "网赌",
        "盘口", "网络赌博", "开户送", "赌具",
        // 色情/涉黄
        "色情", "淫秽", "嫖娼", "卖淫", "招嫖", "约炮", "裸聊", "强奸", "迷奸",
        "福利姬", "卖片", "裸条", "成人网站",
        // 枪支/爆炸物
        "枪支", "弹药", "炸药", "火药", "雷管", "气枪", "仿真枪",
        // 毒品
        "毒品", "冰毒", "海洛因", "摇头丸", "大麻", "可卡因", "罂粟", "制毒", "贩毒",
        "k粉", "麻古", "笑气", "迷魂药", "听话水", "吸毒",
        // 诈骗/黑产
        "诈骗", "骗子", "杀猪盘", "裸贷", "电信诈骗", "集资诈骗", "刷单", "兼职刷单",
        "荐股", "开盒", "人肉搜索", "社工库", "usdt", "炒币", "虚拟币", "假币",
        "盗刷", "洗钱", "跑分", "黑产", "帮信", "卡商", "接码", "养号", "引流",
        // 假证/套现
        "假证", "办证", "刻章", "代开发票", "发票套现", "套现", "代孕", "卖肾",
        "器官买卖", "贩卖人口", "儿童色情", "虐待",
        // 传销/资金盘
        "传销", "资金盘", "庞氏",
        // 恶意软件/入侵
        "木马", "病毒", "钓鱼网站", "盗号", "黑客入侵", "肉鸡", "ddos", "cc攻击",
        "免杀", "远控", "木马程序",
        // 常见繁体变体（NFKC 不转换繁体，直接内置）
        "賭博", "詐騙", "賣淫", "販毒", "洗錢", "殺豬盤", "賣春", "私服外挂"
    ];

    /// <summary>
    /// 清洗用户输入文本：去除控制字符（保留换行）、去掉首尾空白，可选截断到 <paramref name="maxLength"/>（0 表示不截断）。
    /// 用于昵称 / 聊天 / 房间名等入站文本。对应 toolkit.py:31 <c>sanitize_text</c>。
    /// 注意：零宽字符与旧版一样<b>不</b>在这里删（只在 <see cref="CheckPolicy"/> 的归一化里删）。
    /// </summary>
    public static string SanitizeText(string? text, int maxLength = 0)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var value = ControlChars.Replace(text, string.Empty).Replace("\r", string.Empty).Trim();

        if (maxLength > 0 && value.Length > maxLength) value = value[..maxLength];
        return value;
    }

    /// <summary>公网 IP 脱敏（1.2.3.4 → 1.2.*.*），大厅展示时防真实 IP 泄露。toolkit.py:96 <c>mask_ip</c>。</summary>
    public static string MaskIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return string.Empty;

        var parts = ip.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.*.*" : ip;
    }

    /// <summary>
    /// 聊天文本轻量混淆：SHA-256 派生 32 字节密钥流循环异或 + Base64，
    /// 附 4 字节校验标签，错误密钥/损坏数据解密返回空串。密钥不在流量中传输。
    /// 对应 toolkit.py:43 <c>chat_encrypt</c>。
    /// </summary>
    public static string Encrypt(string key, string text)
    {
        try
        {
            var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key ?? string.Empty));
            var salt = RandomNumberGenerator.GetBytes(SaltLength);
            var data = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var body = Xor(data, SHA256.HashData(Concat(keyBytes, salt)));
            var tag = SHA256.HashData(Concat(keyBytes, salt, body));

            var blob = new byte[SaltLength + TagLength + body.Length];
            Buffer.BlockCopy(salt, 0, blob, 0, SaltLength);
            Buffer.BlockCopy(tag, 0, blob, SaltLength, TagLength);
            Buffer.BlockCopy(body, 0, blob, SaltLength + TagLength, body.Length);

            return ToUrlSafeBase64(blob);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>解密 <see cref="Encrypt"/> 的输出。密钥错误或数据损坏返回空串。对应 toolkit.py:68 <c>chat_decrypt</c>。</summary>
    public static string Decrypt(string key, string? payload)
    {
        try
        {
            var raw = FromUrlSafeBase64(payload);
            if (raw.Length < SaltLength + TagLength + 1) return string.Empty;

            var salt = raw[..SaltLength];
            var tag = raw[SaltLength..(SaltLength + TagLength)];
            var body = raw[(SaltLength + TagLength)..];

            var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key ?? string.Empty));
            var expect = SHA256.HashData(Concat(keyBytes, salt, body));

            if (!CryptographicOperations.FixedTimeEquals(tag, expect.AsSpan(0, TagLength))) return string.Empty;

            var plain = Xor(body, SHA256.HashData(Concat(keyBytes, salt)));
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 聊天内容策略检查：严格禁止网址与违禁词。
    /// 网址先做 NFKC 归一化（全角→半角）再三路检测（原文 / 去空白 / 分隔符还原）；
    /// 违禁词在压缩为「字母数字+汉字」后匹配，零宽字符与任意标点插入都无法绕过。
    /// 版本号（v1.2.3）/时间点（9点30）/小数（3.14）不构成域名，不会被误拦。
    /// 对应 toolkit.py:175 <c>chat_policy</c>。
    /// </summary>
    public static (bool Ok, string Reason) CheckPolicy(string? text)
    {
        var value = SanitizeText(text, MaxTextLength);
        if (value.Length == 0) return (true, string.Empty);

        var normalized = NormalizeForDetect(value);

        if (LooksLikeUrl(normalized)) return (false, "网址");

        var compact = NonWord.Replace(normalized.ToLowerInvariant(), string.Empty);

        foreach (var word in BannedWords)
        {
            if (compact.Contains(word.ToLowerInvariant(), StringComparison.Ordinal)) return (false, "违禁词");
        }

        return (true, string.Empty);
    }

    /// <summary>检测用归一化：NFKC 全角→半角，去掉零宽/格式/控制/不可见字符。</summary>
    private static string NormalizeForDetect(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return NormalizeDrops.Replace(normalized, string.Empty);
    }

    /// <summary>网址三路检测：归一化原文 / 去全部空白 / 分隔符伪装还原。</summary>
    private static bool LooksLikeUrl(string normalized)
    {
        if (UrlPattern.IsMatch(normalized)) return true;

        var noSpace = Regex.Replace(normalized, @"\s+", string.Empty);
        if (UrlPattern.IsMatch(noSpace)) return true;

        return UrlPattern.IsMatch(UrlMaskPattern.Replace(noSpace, "."));
    }

    /// <summary>用 32 字节密钥流循环异或（与旧版 Python 实现的循环等价）。</summary>
    private static byte[] Xor(byte[] data, byte[] stream)
    {
        var output = new byte[data.Length];

        for (var i = 0; i < data.Length; i++) output[i] = (byte)(data[i] ^ stream[i % stream.Length]);
        return output;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var buffer = new byte[parts.Sum(part => part.Length)];
        var offset = 0;

        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, buffer, offset, part.Length);
            offset += part.Length;
        }

        return buffer;
    }

    /// <summary>Python 的 base64.urlsafe_b64encode：'+' → '-'，'/' → '_'（保留 '=' 填充）。</summary>
    private static string ToUrlSafeBase64(byte[] data)
        => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_');

    private static byte[] FromUrlSafeBase64(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var value = text.Replace('-', '+').Replace('_', '/');

        // 旧版一定带 '=' 填充，这里补齐只是容错
        switch (value.Length % 4)
        {
            case 2: value += "=="; break;
            case 3: value += "="; break;
        }

        return Convert.FromBase64String(value);
    }
}
