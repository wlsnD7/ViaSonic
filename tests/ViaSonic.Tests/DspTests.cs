using ViaSonic.Audio;

namespace ViaSonic.Tests;

public class DspTests
{
    [Fact]
    public void FftRoundTrip()
    {
        var rng = new Random(1);
        var x = Enumerable.Range(0, 256).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        var re = (float[])x.Clone();
        var im = new float[256];
        Fft.Run(re, im, false);
        Fft.Run(re, im, true);
        for (int i = 0; i < 256; i++) Assert.Equal(x[i], re[i] / 256, 4); // 反变换不做 1/N
    }

    [Fact]
    public void FftFindsTheTone()
    {
        const int n = 512;
        var re = Enumerable.Range(0, n).Select(i => (float)Math.Cos(2 * Math.PI * 20 * i / n)).ToArray();
        var im = new float[n];
        Fft.Run(re, im, false);
        var mag = Enumerable.Range(0, n / 2).Select(k => Math.Sqrt(re[k] * re[k] + im[k] * im[k])).ToList();
        Assert.Equal(20, mag.IndexOf(mag.Max()));
    }

    private static double Gain(Biquad f, double freq, int rate = 16000)
    {
        var x = Signal.Sine(freq, 0.5, rate).Samples;
        var y = x.Select(f.Process).ToArray();
        return Signal.Rms(y.AsSpan(rate / 10)) / Signal.Rms(x.AsSpan(rate / 10));
    }

    [Fact]
    public void LowPassAndHighPass()
    {
        var lp = new Biquad();
        lp.LowPass(16000, 500);
        Assert.InRange(Gain(lp, 100), 0.9, 1.1);
        lp = new Biquad();
        lp.LowPass(16000, 500);
        Assert.True(Gain(lp, 4000) < 0.05);

        var hp = new Biquad();
        hp.HighPass(16000, 1000);
        Assert.True(Gain(hp, 100) < 0.05);
        hp = new Biquad();
        hp.HighPass(16000, 1000);
        Assert.InRange(Gain(hp, 5000), 0.9, 1.1);
    }

    [Fact]
    public void ShelvesBoostByDecibels()
    {
        var low = new Biquad();
        low.LowShelf(16000, 200, 6);
        Assert.InRange(Gain(low, 40), 1.8, 2.2); // +6dB ≈ 2 倍
        var high = new Biquad();
        high.HighShelf(16000, 3500, -6);
        Assert.InRange(Gain(high, 7000), 0.45, 0.6);
    }

    [Fact]
    public void DelayLineInterpolates()
    {
        var d = new DelayLine(16);
        for (int i = 0; i < 10; i++) d.Write(i);
        Assert.Equal(9, d.Read(1));
        Assert.Equal(7, d.Read(3));
        Assert.Equal(8.5f, d.Read(1.5), 4);
    }
}

public class VoiceFxTests
{
    private static VoiceFx.Settings Only(string fx, params (string Key, double Value)[] values)
    {
        var effects = VoiceFx.DefaultEffects();
        effects[fx]!["on"] = true;
        foreach (var (k, v) in values) effects[fx]![k] = v;
        return VoiceFx.Settings.Parse(effects);
    }

    [Fact]
    public void ParseTakesOnlyEnabledEffectsAndClamps()
    {
        var effects = VoiceFx.DefaultEffects();
        Assert.Empty(VoiceFx.Settings.Parse(effects).On);
        effects["pitch"]!["on"] = true;
        effects["pitch"]!["semitones"] = 99;
        effects["echo"]!["on"] = true;
        effects["echo"]!.AsObject().Remove("delay");
        var s = VoiceFx.Settings.Parse(effects);
        Assert.Equal(["echo", "pitch"], s.On.Keys.Order());
        Assert.Equal(12, s.Get("pitch", 0));     // 超出范围夹到最大
        Assert.Equal(250, s.Get("echo", 0));     // 缺的参数用默认值
        Assert.Empty(VoiceFx.Settings.Parse(null).On);
    }

    [Fact]
    public void DefaultEffectsCoverEveryEffect()
    {
        var effects = VoiceFx.DefaultEffects();
        foreach (var def in VoiceFx.Defs)
        {
            Assert.False(effects[def.Key].Bool("on", true));
            foreach (var p in def.Params) Assert.Equal(p.Default, effects[def.Key].Num(p.Key));
        }
    }

    [Fact]
    public void NothingOnReturnsSameClip()
    {
        var clip = Signal.Sine(200, 0.1);
        Assert.Same(clip, VoiceFx.ProcessClip(clip, VoiceFx.Settings.None));
    }

    public static IEnumerable<object[]> AllEffects() => VoiceFx.Defs.Select(d => new object[] { d.Key });

    [Theory]
    [MemberData(nameof(AllEffects))]
    public void EveryEffectGivesSaneOutput(string fx)
    {
        var clip = Signal.Sine(220, 0.5, 24000, 0.9f);
        var y = VoiceFx.ProcessClip(clip, Only(fx)).Samples;
        Assert.True(y.Length >= clip.Samples.Length);
        Assert.All(y, x => Assert.True(float.IsFinite(x)));
        Assert.True(Signal.Peak(y) <= 1.0f, $"{fx} 超过 0dB"); // 限幅器一直开着
    }

    [Fact]
    public void PitchUpAnOctave()
    {
        const int rate = 24000;
        var clip = Signal.Sine(220, 1, rate);
        var y = VoiceFx.ProcessClip(clip, Only("pitch", ("semitones", 12))).Samples;
        Assert.Equal(clip.Samples.Length, y.Length); // 音高处理的延迟已经去掉
        Assert.InRange(Signal.Frequency(y.AsSpan(rate / 5, rate / 2), rate), 440 * 0.95, 440 * 1.05);
    }

    [Fact]
    public void EchoLeavesATail()
    {
        var clip = Signal.Sine(220, 0.3);
        var y = VoiceFx.ProcessClip(clip, Only("echo")).Samples;
        Assert.True(y.Length > clip.Samples.Length);
    }
}
