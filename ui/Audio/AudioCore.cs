using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace ViaTTS.Audio;

/// <summary>一段单声道音频。</summary>
public sealed record Clip(float[] Samples, int SampleRate)
{
    public Clip WithLeadSilence(int ms)
    {
        int n = SampleRate * ms / 1000;
        if (n <= 0) return this;
        var s = new float[n + Samples.Length];
        Array.Copy(Samples, 0, s, n, Samples.Length);
        return new Clip(s, SampleRate);
    }
}

public static class AudioDecode
{
    private static bool _mf;

    private static void EnsureMf()
    {
        if (_mf) return;
        MediaFoundationApi.Startup();
        _mf = true;
    }

    public static Clip FromWave(byte[] wav)
    {
        using var r = new WaveFileReader(new MemoryStream(wav));
        return Read(r);
    }

    /// <summary>MP3 等压缩格式的内存数据（Edge TTS 返回 MP3）。</summary>
    public static Clip FromCompressed(byte[] data)
    {
        EnsureMf();
        using var r = new StreamMediaFoundationReader(new MemoryStream(data));
        return Read(r);
    }

    /// <summary>常用语的自定义音频：wav / mp3 / m4a / flac / wma 等，交给 Media Foundation 解码。</summary>
    public static Clip FromFile(string path)
    {
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var w = new WaveFileReader(path);
                return Read(w);
            }
            catch { } // 不常见的 wav 编码再交给 Media Foundation
        }
        EnsureMf();
        using var r = new MediaFoundationReader(path);
        return Read(r);
    }

    private static Clip Read(WaveStream stream)
    {
        var sp = stream.ToSampleProvider();
        int ch = sp.WaveFormat.Channels;
        var all = new List<float>(sp.WaveFormat.SampleRate * 4);
        var buf = new float[sp.WaveFormat.SampleRate * ch];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i + ch <= n; i += ch)
            {
                float sum = 0;
                for (int c = 0; c < ch; c++) sum += buf[i + c];
                all.Add(sum / ch);
            }
        }
        return new Clip(all.ToArray(), sp.WaveFormat.SampleRate);
    }
}

public static class Devices
{
    public static List<string> OutputNames()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => d.FriendlyName).ToList();
    }

    public static string VirtualCable()
    {
        var names = OutputNames();
        return names.FirstOrDefault(n => n.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
               ?? names.FirstOrDefault(n => n.Contains("CABLE", StringComparison.OrdinalIgnoreCase)
                                            || n.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase))
               ?? "";
    }

    /// <summary>按名称找设备；名称为空时返回系统默认输出。</summary>
    public static MMDevice? Find(MMDeviceEnumerator en, string name)
    {
        if (string.IsNullOrEmpty(name))
            return en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : null;
        var all = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        return all.FirstOrDefault(d => d.FriendlyName == name)
               ?? all.FirstOrDefault(d => d.FriendlyName.StartsWith(name) || name.StartsWith(d.FriendlyName));
    }
}
