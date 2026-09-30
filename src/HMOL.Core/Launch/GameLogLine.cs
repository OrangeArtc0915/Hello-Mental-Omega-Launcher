namespace HMOL.Core.Launch;

/// <summary>一条游戏输出。等级直接复用日志的等级，界面可以原样转给 ActivityLog。</summary>
public sealed record GameLogLine(DateTime Time, Logging.ActivityLevel Level, string Text);
