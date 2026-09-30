namespace HMOL.Core.Games;

/// <summary>
/// 实例对应的游戏类型。决定两件事：用哪个默认主程序名去探测（见 <see cref="GameLocator"/>），
/// 以及实例卡片上显示哪个图标。
/// </summary>
public enum GameKind
{
    /// <summary>心灵终结（默认）。</summary>
    MentalOmega = 0,

    /// <summary>红色警戒 2 原版。</summary>
    OriginalRa2 = 1,

    /// <summary>尤里的复仇。</summary>
    YurisRevenge = 2,

    /// <summary>其它红警 Mod：主程序由用户自己指定。</summary>
    Other = 3
}

/// <summary>游戏类型的界面名称。旧配置文件里没有类型字段时按心灵终结处理。</summary>
public static class GameKinds
{
    /// <summary>全部类型，按界面展示顺序排列。</summary>
    public static readonly GameKind[] All =
    [
        GameKind.MentalOmega,
        GameKind.OriginalRa2,
        GameKind.YurisRevenge,
        GameKind.Other
    ];

    public static string DisplayName(GameKind kind) => kind switch
    {
        GameKind.OriginalRa2 => "原版（红色警戒2）",
        GameKind.YurisRevenge => "尤里的复仇",
        GameKind.Other => "其它红警 Mod",
        _ => "心灵终结"
    };
}