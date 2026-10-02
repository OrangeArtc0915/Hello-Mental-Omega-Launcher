namespace HMOL.Core.IO;

/// <summary>
/// 一次进度汇报。<paramref name="Fraction"/> 是**整体**进度（0~1），
/// <paramref name="DoneBytes"/> / <paramref name="TotalBytes"/> 是「当前这一段」的字节明细，
/// 用来算实时速度；这一段不掌握字节数时 TotalBytes 为 0，上层就只按分数估。
/// </summary>
public readonly record struct ProgressSample(double Fraction, long DoneBytes = 0, long TotalBytes = 0)
{
    /// <summary>这一段是否掌握字节数。</summary>
    public bool HasBytes => TotalBytes > 0;
}

/// <summary>
/// 把子步骤的 0~1 进度映射到整体进度的某一段，便于一个操作里串联解压 / 复制 / 校验等多段。
/// 字节明细原样透传，上层据此显示速度与预计剩余时间。
/// </summary>
internal sealed class ProgressSpan(IProgress<ProgressSample> inner, double from, double to)
    : IProgress<ProgressSample>
{
    public void Report(ProgressSample value)
        => inner.Report(value with { Fraction = from + Math.Clamp(value.Fraction, 0, 1) * (to - from) });
}

/// <summary>
/// 把「只知道百分比」的进度补上字节明细：按给定总字节数把百分比换算成已处理字节，
/// 这样上层照样能显示速度。<paramref name="totalBytes"/> 为 0（总量未知）时只透传百分比。
/// </summary>
internal sealed class PercentProgress(IProgress<ProgressSample> inner, long totalBytes = 0) : IProgress<double>
{
    public void Report(double value)
    {
        var fraction = Math.Clamp(value, 0, 1);
        inner.Report(new ProgressSample(fraction, (long)(fraction * totalBytes), totalBytes));
    }
}
