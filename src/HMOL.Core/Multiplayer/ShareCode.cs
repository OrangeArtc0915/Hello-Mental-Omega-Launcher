namespace HMOL.Core.Multiplayer;

/// <summary>从分享文本里解析出的组网参数。</summary>
/// <param name="Kind">识别出的引擎（ZeroTier / Anywherelan 已裁剪，一律回落到 EasyTier）。</param>
/// <param name="PlanRaw">「方案」行的原文，供界面提示旧版本不兼容。</param>
/// <param name="IsOwner">
/// 「房主」行的解析值，语义照搬旧版 share.py:78（<c>v != '是'</c>），
/// 因此分享文本写的「房主: 否」解析出来是 <c>true</c>。
/// <b>不要用它决定房主</b>：旧版 <c>_apply_share</c> 也是无视它、一律按成员加入，新版界面同样应一律按成员加入。
/// </param>
public sealed record ShareInfo(
    NetworkEngineKind Kind,
    string PlanRaw,
    string Node,
    string RoomName,
    string Key,
    bool IsOwner,
    NetworkAddressMode AddressMode,
    string ManualIp,
    string? Version);

/// <summary>
/// 组网分享文本的生成与解析。对应旧版 share.py:19 <c>build_share_text</c> / :42 <c>parse_share_text</c>。
///
/// <para>
/// 文本格式与旧版一致（标签：版本 / 方案 / 节点 / 房间名 / 密钥 / 房主 / IP），
/// 旧版客户端发出的分享文本可以直接解析；解析兼容旧版的「小组:」标签与缺少版本行的文本。
/// 二维码绘制属于界面层（需要额外图形库），不在本轮范围。
/// </para>
/// </summary>
public static class ShareCode
{
    /// <summary>分享文本首行。</summary>
    public const string Header = "HMOL联机模块 组网分享";

    /// <summary>生成分享文本。「房主: 否」表示通过此文本加入的玩家是成员而非房主。</summary>
    public static string Build(
        NetworkEngineKind kind,
        string? node,
        string? roomName,
        string? key,
        NetworkAddressMode addressMode,
        string? manualIp,
        string version = "")
    {
        var planName = kind == NetworkEngineKind.N2n ? "n2n" : "EasyTier";
        var ipText = addressMode == NetworkAddressMode.Manual ? $"手动:{manualIp}" : "自动";
        var keyText = string.IsNullOrEmpty(key) ? "(无)" : key;
        var versionLine = string.IsNullOrEmpty(version) ? string.Empty : $"版本: {version}\n";

        return $"{Header}\n"
               + versionLine
               + $"方案: {planName}\n"
               + $"节点: {node}\n"
               + $"房间名: {roomName}\n"
               + $"密钥: {keyText}\n"
               + "房主: 否\n"
               + $"IP: {ipText}\n"
               + "官方QQ群：\n"
               + "    1群：1034243331\n"
               + "从其他渠道下载的资源我们无法保障其安全性！";
    }

    /// <summary>解析分享文本；缺少方案 / 房间名 / 节点时返回 null。</summary>
    public static ShareInfo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var plan = string.Empty;
        var version = string.Empty;
        var node = string.Empty;
        var roomName = string.Empty;
        var key = string.Empty;
        var owner = true;
        var addressMode = NetworkAddressMode.Auto;
        var manualIp = string.Empty;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var index = line.IndexOf(':');
            if (index <= 0) continue;

            var name = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (value.Length == 0) continue;

            switch (name)
            {
                case "方案":
                    plan = value.ToLowerInvariant();
                    break;

                case "版本":
                    version = value;
                    break;

                case "节点":
                    node = value;
                    break;

                case "房间名":
                case "小组":
                    roomName = value;
                    break;

                case "密钥":
                    key = value is "(无)" or "(可选)" ? string.Empty : value;
                    break;

                case "房主":
                    owner = value != "是";
                    break;

                case "IP":
                    if (value == "自动")
                    {
                        addressMode = NetworkAddressMode.Auto;
                    }
                    else if (value.StartsWith("手动", StringComparison.Ordinal))
                    {
                        addressMode = NetworkAddressMode.Manual;
                        var separator = value.IndexOf(':');
                        manualIp = separator >= 0 ? value[(separator + 1)..].Trim() : string.Empty;
                    }

                    break;
            }
        }

        if (roomName.Length == 0 || node.Length == 0) return null;

        // 旧版把 ZeroTier / Anywherelan 也写进「方案」；这两个引擎已删除，这里保留原文供界面提示版本不兼容
        var kind = plan == "n2n" ? NetworkEngineKind.N2n : NetworkEngineKind.EasyTier;

        return new ShareInfo(
            kind,
            plan.Length == 0 ? "easytier" : plan,
            node,
            roomName,
            key,
            owner,
            addressMode,
            manualIp,
            version.Length == 0 ? null : version);
    }
}
