using System.Text.Json.Serialization;
using HMOL.Core.Security;

namespace HMOL.Core.Multiplayer;

/// <summary>
/// 收藏的队友。字段对应旧版 friends.py 里存进配置的 dict：
/// name / ip / community / times / last_seen（新版 last_seen 由 Unix 秒改为本地时间，便于界面直接显示）。
/// </summary>
public sealed class FriendEntry
{
    public string Name { get; set; } = string.Empty;

    public string Ip { get; set; } = string.Empty;

    /// <summary>最近一次一起联机的房间名。</summary>
    public string Community { get; set; } = string.Empty;

    /// <summary>一起联机的次数。</summary>
    public int Times { get; set; }

    public DateTime LastSeen { get; set; } = DateTime.Now;
}

/// <summary>
/// 联机模块自己的配置。对应旧版 config.py 的 Config / DEFAULTS，按范围裁剪：
/// 删掉 ZeroTier（zt_*）、Anywherelan（awl_*）、HUD（hud_*）、主题、EULA、关闭动作等
/// 已废弃或已挪到全局设置的项目。
///
/// 落盘到 <c>Data\multiplayer.json</c>，与全局 <c>Data\Settings.json</c> 分开，互不影响。
/// 昵称不在这里：继续用全局 Settings.Nickname（旧版 config.py 里那份 nickname 与主程序重复）。
/// </summary>
public sealed class MultiplayerSettings
{
    public int Version { get; set; } = 1;

    /// <summary>上次使用的组网引擎。</summary>
    public NetworkEngineKind Engine { get; set; } = NetworkEngineKind.EasyTier;

    /// <summary>EasyTier 上次使用的节点。</summary>
    public string EasyTierNode { get; set; } = "udp://39.108.52.138:11010";

    /// <summary>n2n 上次使用的节点。</summary>
    public string N2nNode { get; set; } = "n2n.aobacore.com:9555";

    /// <summary>房间名（旧版的 community / 小组名）。</summary>
    public string RoomName { get; set; } = string.Empty;

    /// <summary>房间密钥，可留空。落盘时经 DPAPI 加密，不带明文进配置文件。</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string RoomKey { get; set; } = string.Empty;

    /// <summary>虚拟 IP 分配方式。</summary>
    public NetworkAddressMode AddressMode { get; set; } = NetworkAddressMode.Auto;

    /// <summary>手工虚拟 IP（AddressMode 为 Manual 时生效）。</summary>
    public string ManualIp { get; set; } = "192.168.100.66";

    /// <summary>是否把本房间公开给联机大厅（旧版界面的「公开房间」勾选，新版随配置持久化）。</summary>
    public bool PublishRoom { get; set; }

    /// <summary>用户自定义节点（内置节点之外的补充）。</summary>
    public List<string> CustomNodes { get; set; } = [];

    /// <summary>收藏的队友。</summary>
    public List<FriendEntry> Friends { get; set; } = [];

    /// <summary>进程守护开关。</summary>
    public bool GuardEnabled { get; set; } = true;

    /// <summary>开机自启开关。</summary>
    public bool AutoStart { get; set; }

    /// <summary>自建 supernode 的监听端口。</summary>
    public int SupernodePort { get; set; } = 9555;

    /// <summary>自建 supernode 的自动分配 IP 池。</summary>
    public string SupernodeIpPool { get; set; } = NodeCatalog.N2nIpPool;

    /// <summary>去掉空白与重复项后的自定义节点列表。</summary>
    public IReadOnlyList<string> ValidCustomNodes =>
        CustomNodes.Where(node => !string.IsNullOrWhiteSpace(node))
            .Select(node => node.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>游戏内 HUD 上次关闭时的左坐标（屏幕设备无关单位）。为 null 表示还没记录过，用默认落点。</summary>
    public double? HudLeft { get; set; }

    /// <summary>游戏内 HUD 上次关闭时的上坐标。为 null 表示还没记录过，用默认落点。</summary>
    public double? HudTop { get; set; }
}
