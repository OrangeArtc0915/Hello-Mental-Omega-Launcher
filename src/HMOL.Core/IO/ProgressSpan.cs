namespace HMOL.Core.IO;

/// <summary>
/// 把子步骤的 0~1 进度映射到整体进度的某一段，便于一个操作里串联解压 / 复制 / 校验等多段。
/// </summary>
internal sealed class ProgressSpan(IProgress<double> inner, double from, double to) : IProgress<double>
{
    public void Report(double value)
        => inner.Report(from + Math.Clamp(value, 0, 1) * (to - from));
}
