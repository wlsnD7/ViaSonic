using NAudio.Wave;
using ViaSonic.Audio;

namespace ViaSonic.Tests;

public class StretchTests
{
    private static float[] Run(Clip clip, double speed, List<int>? src = null)
    {
        var st = new Stretch(clip, 0);
        var outp = new List<float>();
        while (st.Next(speed, out var y, out var s))
        {
            outp.Add(y);
            src?.Add(s);
        }
        return [.. outp];
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(0.75)]
    [InlineData(0.5)]
    public void ChangesLengthKeepsPitch(double speed)
    {
        const int rate = 24000;
        var clip = Signal.Sine(220, 2, rate);
        var y = Run(clip, speed);
        Assert.InRange(y.Length, clip.Samples.Length / speed * 0.95, clip.Samples.Length / speed * 1.05);
        // 去掉开头结尾各 0.1 秒的淡入淡出
        var mid = y.AsSpan(rate / 10, y.Length - rate / 5);
        Assert.InRange(Signal.Frequency(mid, rate), 220 * 0.97, 220 * 1.03);
        Assert.InRange(Signal.Rms(mid), Signal.Rms(clip.Samples) * 0.8, Signal.Rms(clip.Samples) * 1.2);
    }

    [Fact]
    public void SourcePositionMovesForward()
    {
        var clip = Signal.Sine(300, 1);
        var src = new List<int>();
        Run(clip, 1.25, src);
        for (int i = 1; i < src.Count; i++) Assert.True(src[i] >= src[i - 1] - 1);
        Assert.InRange(src[^1], 0, clip.Samples.Length);
    }
}

public class VoiceTests
{
    private const int Rate = 1000;

    /// <summary>0, 0.001, 0.002 …：从读出来的值就能看出播到了哪里。</summary>
    private static Clip Ramp(int n) => new(Enumerable.Range(0, n).Select(i => i / 1000f).ToArray(), Rate);

    private static Voice Make(Clip clip, int leadMs = 0, bool loop = false, float gain = 1, int start = 0, Func<double>? speed = null) =>
        new(clip, leadMs, () => loop, mon => mon ? gain / 2 : gain, start, speed);

    private static float[] Read(ISampleProvider p, int count, out int got)
    {
        var buf = new float[count];
        got = p.Read(buf, 0, count);
        return buf;
    }

    [Fact]
    public void LeadSilenceThenClip()
    {
        var v = Make(Ramp(500), leadMs: 100, gain: 2);
        var buf = Read(v.Stream(() => 1, false), 1000, out var got);
        Assert.Equal(600, got);
        Assert.All(buf[..100], x => Assert.Equal(0, x));
        Assert.Equal(0.002f, buf[101], 5);
        Assert.Equal(0.998f, buf[599], 5);
    }

    [Fact]
    public void MonitorAndDeviceVolume()
    {
        var v = Make(Ramp(100), gain: 1);
        var buf = Read(v.Stream(() => 0.5f, true), 100, out _);
        Assert.Equal(0.050f * 0.5f * 0.5f, buf[50], 5);
    }

    [Fact]
    public void StartOffsetAndLoop()
    {
        var v = Make(Ramp(100), loop: true, start: 40);
        var buf = Read(v.Stream(() => 1, false), 200, out var got);
        Assert.Equal(200, got);
        Assert.Equal(0.040f, buf[0], 5);
        Assert.Equal(0.000f, buf[60], 5); // 播完从头循环
        Assert.Equal(0.020f, buf[180], 5);
    }

    [Fact]
    public void SeekRampsIn()
    {
        var v = Make(Ramp(1000));
        var s = v.Stream(() => 1, false);
        Read(s, 100, out _);
        v.Seek(800);
        var buf = Read(s, 100, out _);
        Assert.True(buf[0] < 0.1f);                  // 跳转后 30ms 淡入
        Assert.Equal(0.850f, buf[50], 5);
        Assert.Equal(900, v.Position); // 跳到 800 后又读了 100 个
    }

    [Fact]
    public void FadeStopEndsStream()
    {
        var clip = new Clip(Enumerable.Repeat(0.5f, 1000).ToArray(), Rate);
        var v = Make(clip);
        var s = v.Stream(() => 1, false);
        Read(s, 100, out _);
        v.FadeStop(50);
        var buf = Read(s, 500, out var got);
        Assert.Equal(50, got);
        Assert.True(buf[0] > buf[25] && buf[25] > buf[49]);
        Assert.True(v.Stopped);
        Assert.Equal(0, s.Read(new float[10], 0, 10));
    }

    [Fact]
    public void DoneAfterAllStreamsEnd()
    {
        var v = Make(Ramp(10));
        v.Begin(2);
        Assert.False(v.Done.WaitOne(0));
        v.StreamEnded();
        Assert.False(v.Done.WaitOne(0));
        v.StreamEnded();
        Assert.True(v.Done.WaitOne(0));
    }

    [Fact]
    public void StopEndsImmediately()
    {
        var v = Make(Ramp(100));
        var s = v.Stream(() => 1, false);
        v.Stop();
        Assert.Equal(0, s.Read(new float[10], 0, 10));
        Assert.True(v.Done.WaitOne(0));
    }

    [Fact]
    public void NoOutputDevicesFinishesAtOnce()
    {
        var v = Make(Ramp(100));
        v.Begin(0);
        Assert.True(v.Done.WaitOne(0));
    }

    [Fact]
    public void SpeedUpShortensPlayback()
    {
        var clip = Signal.Sine(200, 1, 8000);
        var v = new Voice(clip, 0, () => false, _ => 1, 0, () => 2.0);
        var s = v.Stream(() => 1, false);
        var buf = new float[20000];
        int total = 0, n;
        while ((n = s.Read(buf, total, Math.Min(512, buf.Length - total))) > 0) total += n;
        Assert.InRange(total, 3800, 4200);
    }
}

public class RingTests
{
    private static byte[] Pcm16(params short[] samples)
    {
        var b = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, b, 0, b.Length);
        return b;
    }

    [Fact]
    public void WaitsUntilPrimedThenPlays()
    {
        var ring = new Ring(1000, () => 1);   // 积到 20ms（20 个采样）才输出
        var wf = new WaveFormat(1000, 16, 1);
        ring.Write(Pcm16(Enumerable.Repeat((short)16384, 10).ToArray()), 20, wf);
        var buf = new float[10];
        Assert.Equal(10, ring.Read(buf, 0, 10));
        Assert.All(buf, x => Assert.Equal(0, x));
        ring.Write(Pcm16(Enumerable.Repeat((short)16384, 20).ToArray()), 40, wf);
        ring.Read(buf, 0, 10);
        Assert.All(buf, x => Assert.Equal(0.5f, x, 4));
    }

    [Fact]
    public void MixesStereoToMonoAndAppliesVolume()
    {
        var ring = new Ring(1000, () => 0.5f);
        var wf = new WaveFormat(1000, 16, 2);
        var frames = Enumerable.Range(0, 30).SelectMany(_ => new short[] { 16384, 0 }).ToArray();
        ring.Write(Pcm16(frames), frames.Length * 2, wf);
        var buf = new float[5];
        ring.Read(buf, 0, 5);
        Assert.All(buf, x => Assert.Equal(0.125f, x, 4));
    }

    [Fact]
    public void DropsBacklogToKeepLatencyLow()
    {
        var ring = new Ring(1000, () => 1);   // 积压超过 100ms 时只留最近的 30ms
        var data = Enumerable.Range(0, 150).Select(i => (float)i).ToArray();
        ring.WriteMono(data, 0, data.Length);
        var buf = new float[40];
        ring.Read(buf, 0, 40);
        Assert.Equal(120, buf[0]);
        Assert.Equal(149, buf[29]);
        Assert.Equal(0, buf[30]); // 读空了补静音
    }

    [Fact]
    public void ClosedRingEndsStream()
    {
        var ring = new Ring(1000, () => 1);
        ring.Close();
        Assert.Equal(0, ring.Read(new float[10], 0, 10));
    }
}
