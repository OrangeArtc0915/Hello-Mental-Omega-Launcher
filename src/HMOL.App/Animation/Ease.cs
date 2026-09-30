namespace HMOL.App.Animation;

/// <summary>缓动函数族。参数 t 为 0~1 的归一化进度。</summary>
public static class Ease
{
    public static double Linear(double t) => t;

    public static double InFluent(double t) => InFluent(t, 3);

    public static double InFluent(double t, double power) => Math.Pow(t, power);

    public static double OutFluent(double t) => OutFluent(t, 3);

    public static double OutFluent(double t, double power) => 1 - Math.Pow(1 - t, power);

    public static double InOutFluent(double t) => InOutFluent(t, 3);

    public static double InOutFluent(double t, double power)
        => t < 0.5
            ? Math.Pow(t * 2, power) / 2
            : 1 - Math.Pow((1 - t) * 2, power) / 2;

    /// <summary>越过终点后回弹，用于列表项进入、按钮回弹等场景。</summary>
    public static double OutBack(double t)
    {
        const double c1 = 1.70158;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    public static double OutElastic(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        const double c4 = 2 * Math.PI / 3;
        return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * c4) + 1;
    }
}
