using ViaSonic;
using ViaSonic.Audio;

// 界面语言是静态的，测试改它时不能和别的测试同时跑
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ViaSonic.Tests;

/// <summary>临时切换界面语言，结束时换回中文。</summary>
internal sealed class LangScope : IDisposable
{
    public LangScope(string lang) => Lang.Init(lang);
    public void Dispose() => Lang.Init("zh");
}

internal static class Signal
{
    public static Clip Sine(double freq, double seconds, int rate = 24000, float amp = 0.5f)
    {
        var s = new float[(int)(seconds * rate)];
        for (int i = 0; i < s.Length; i++) s[i] = amp * (float)Math.Sin(2 * Math.PI * freq * i / rate);
        return new Clip(s, rate);
    }

    public static float Peak(ReadOnlySpan<float> s)
    {
        float p = 0;
        foreach (var x in s) p = Math.Max(p, Math.Abs(x));
        return p;
    }

    public static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return s.Length == 0 ? 0 : Math.Sqrt(sum / s.Length);
    }

    /// <summary>用过零次数估计频率。</summary>
    public static double Frequency(ReadOnlySpan<float> s, int rate)
    {
        int crossings = 0;
        for (int i = 1; i < s.Length; i++)
            if ((s[i - 1] < 0) != (s[i] < 0)) crossings++;
        return crossings / 2.0 / ((double)s.Length / rate);
    }

    /// <summary>仓库根目录（找 ui\ViaSonic.csproj）。</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ui", "ViaSonic.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录");
    }
}
