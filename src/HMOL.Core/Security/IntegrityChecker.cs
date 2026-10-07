using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HMOL.Core.Localization;

namespace HMOL.Core.Security;

/// <summary>
/// 完整性比对结果。
/// <param name="Intact">true 表示所有配了校验值的文件都相符。</param>
/// <param name="Problems">文件缺失 / 读不到 / 校验值不符的条目。</param>
/// <param name="NotConfigured">没有校验文件、按旧版口径跳过比对的条目（不算问题）。</param>
/// </summary>
public sealed record TamperReport(bool Intact, IReadOnlyList<string> Problems, IReadOnlyList<string> NotConfigured);

/// <summary>
/// 自身哈希与完整性校验。
/// 移植自旧版 <c>HMOL_QT/anti_debug.py</c> 的 <c>get_self_hash()</c> / <c>check_tampering()</c>
/// 与 <c>_resolve_integrity_key()</c>。只返回结果，不打日志、不弹窗。
/// </summary>
public static class IntegrityChecker
{
    /// <summary>HMAC 密钥环境变量名，与旧版一致。</summary>
    public const string KeyEnvVar = "HMOL_INTEGRITY_KEY";

    /// <summary>校验值文件后缀，与旧版一致：<c>&lt;文件&gt;.sig</c>。</summary>
    public const string SignatureSuffix = ".sig";

    /// <summary>
    /// 旧版 crypto_utils.py 里的内置兜底密钥（OBF1 混淆字面量 + 固定 XOR 密钥）。
    /// 它不是真秘密，只是抬高静态打补丁的门槛，这里保持与旧版同一取值。
    /// </summary>
    private const string ObfuscatedKey = "OBF1:BSIrdgAELzokIDc7dC8fWAMOfGJ2fV1SF2AILzogKyo/ZTFz";

    /// <summary>旧版 deobfuscate_string() 用的 XOR 密钥（ASCII "Mod:-Manager v2.1 MOD"）。</summary>
    private static readonly byte[] XorKey = Encoding.ASCII.GetBytes("Mod:-Manager v2.1 MOD");

    /// <summary>
    /// 正在运行的 exe 路径。
    /// 自包含单文件发布（build.bat 的 PublishSingleFile）下 Assembly.Location 为空，
    /// AppContext.BaseDirectory 也不保证等于 exe 所在目录，都不能代表程序本体；
    /// Environment.ProcessPath（.NET 6+）才稳定给出单文件 exe 的真实路径，故优先使用它。
    /// </summary>
    public static string? SelfPath { get; } = ResolveSelfPath();

    /// <summary>当前 exe 的 SHA-256（小写十六进制）；读不到时返回空串。对应旧版 get_self_hash()。</summary>
    public static string SelfSha256()
        => SelfPath is { } path ? HexDigestOfFile(path, hmacKey: null) : string.Empty;

    /// <summary>当前 exe 的字节数；读不到时返回 -1。</summary>
    public static long SelfSize()
    {
        try
        {
            return SelfPath is { } path ? new FileInfo(path).Length : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// 逐文件比对 <c>&lt;文件&gt;.sig</c> 里记录的 HMAC-SHA256。
    /// 与旧版口径一致：没有 .sig 就跳过比对（记进 NotConfigured）；文件缺失 / 读不到 / 哈希不符记进 Problems。
    /// </summary>
    public static TamperReport CheckTampering(IEnumerable<string> files)
    {
        var problems = new List<string>();
        var notConfigured = new List<string>();
        var key = ResolveKey();

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;

            if (!File.Exists(file))
            {
                problems.Add(Loc.F("{0}（文件缺失）", file));
                continue;
            }

            var digest = HexDigestOfFile(file, key);
            if (digest.Length == 0)
            {
                problems.Add(Loc.F("{0}（无法读取）", file));
                continue;
            }

            var sigPath = file + SignatureSuffix;
            if (!File.Exists(sigPath))
            {
                notConfigured.Add(Loc.F("{0} 未配置校验文件，跳过比对", Path.GetFileName(file)));
                continue;
            }

            string expected;
            try
            {
                expected = File.ReadAllText(sigPath).Trim();
            }
            catch (Exception)
            {
                notConfigured.Add(Loc.F("{0} 的校验文件读取失败，跳过比对", Path.GetFileName(file)));
                continue;
            }

            if (!FixedTimeEquals(digest, expected)) problems.Add(Loc.F("{0}（HMAC 不匹配）", file));
        }

        return new TamperReport(problems.Count == 0, problems, notConfigured);
    }

    /// <summary>HMAC 密钥：优先取环境变量（支持热切换），否则用内置兜底密钥。对应旧版 _resolve_integrity_key()。</summary>
    private static byte[] ResolveKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(KeyEnvVar);
        return string.IsNullOrEmpty(fromEnvironment)
            ? Encoding.UTF8.GetBytes(Deobfuscate(ObfuscatedKey))
            : Encoding.UTF8.GetBytes(fromEnvironment);
    }

    /// <summary>旧版 crypto_utils.deobfuscate_string() 的等价实现：OBF1 前缀 = Base64 解码后再按固定密钥 XOR。</summary>
    private static string Deobfuscate(string obfuscated)
    {
        if (!obfuscated.StartsWith("OBF1:", StringComparison.Ordinal)) return obfuscated;

        try
        {
            var data = Convert.FromBase64String(obfuscated[5..]);
            var plain = new byte[data.Length];
            for (var i = 0; i < data.Length; i++) plain[i] = (byte)(data[i] ^ XorKey[i % XorKey.Length]);
            return Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return obfuscated; // 解不开就原样返回，与旧版一致
        }
    }

    /// <summary>文件的摘要（小写十六进制）；hmacKey 为 null 时是纯 SHA-256，否则是 HMAC-SHA256。</summary>
    private static string HexDigestOfFile(string path, byte[]? hmacKey)
    {
        try
        {
            using HashAlgorithm algorithm = hmacKey is null ? SHA256.Create() : new HMACSHA256(hmacKey);
            // ComputeHash 内部就是分块读，不会把整个 exe 读进内存
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>常量时间比较，避免按字节短路（旧版用 hmac.compare_digest）。</summary>
    private static bool FixedTimeEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    private static string? ResolveSelfPath()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath)) return processPath;
        }
        catch (Exception)
        {
            // 取不到就往下试别的办法
        }

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
