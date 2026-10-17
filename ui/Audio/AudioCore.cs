using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace ViaSonic.Audio;

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

    /// <summary>只读文件头得到时长，不解码。</summary>
    public static TimeSpan Duration(string path)
    {
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var w = new WaveFileReader(path);
                return w.TotalTime;
            }
            catch { }
        }
        EnsureMf();
        using var r = new MediaFoundationReader(path);
        return r.TotalTime;
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

/// <summary>把合成好的音频存成文件（常用语导出）。</summary>
public static class AudioEncode
{
    /// <summary>按扩展名存成 .wav（16 位 PCM）或 .mp3（Windows 自带的 Media Foundation 编码器，192kbps）。</summary>
    public static void Save(Clip clip, string path)
    {
        var mono = new ClipProvider(clip);
        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            // MP3 编码器只接受 32k/44.1k/48k 的 16 位 PCM
            ISampleProvider sp = mono;
            if (clip.SampleRate is not (32000 or 44100 or 48000)) sp = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sp, 44100);
            MediaFoundationApi.Startup();
            MediaFoundationEncoder.EncodeToMp3(new NAudio.Wave.SampleProviders.SampleToWaveProvider16(sp), path, 192000);
            StripId3(path);
        }
        else
        {
            WaveFileWriter.CreateWaveFile16(path, mono);
        }
    }

    /// <summary>
    /// 去掉编码器写在开头的 ID3 标签（带 iTunSMPB 注释）：Windows 自带的「媒体播放器」遇到它会停在 0:00 不播放，
    /// 去掉后各播放器都正常。
    /// </summary>
    private static void StripId3(string path)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 10 || d[0] != 'I' || d[1] != 'D' || d[2] != '3') return;
        int size = (d[6] & 0x7F) << 21 | (d[7] & 0x7F) << 14 | (d[8] & 0x7F) << 7 | (d[9] & 0x7F);
        int skip = 10 + size + ((d[5] & 0x10) != 0 ? 10 : 0); // 有页脚时再多 10 字节
        if (skip >= d.Length) return;
        File.WriteAllBytes(path, d[skip..]);
    }

    private sealed class ClipProvider(Clip clip) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, clip.Samples.Length - _pos);
            // 和播放一样：buffer 可能是 WaveBuffer 别名，不用 Array.Copy
            for (int i = 0; i < n; i++) buffer[offset + i] = clip.Samples[_pos + i];
            _pos += n;
            return n;
        }
    }
}

public static class Devices
{
    public static List<string> OutputNames()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => d.FriendlyName).ToList();
    }

    /// <summary>录音设备，排除 VB-Cable 自己的 CABLE Output（直通它会形成回环）。</summary>
    public static List<string> InputNames()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => d.FriendlyName).Where(n => !IsCable(n)).ToList();
    }

    public static bool IsCable(string name) => name.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase);

    /// <summary>按名称找录音设备；名称为空时返回系统默认录音设备。</summary>
    public static MMDevice? FindInput(MMDeviceEnumerator en, string name)
    {
        if (string.IsNullOrEmpty(name))
            return en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : null;
        var all = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        return all.FirstOrDefault(d => d.FriendlyName == name)
               ?? all.FirstOrDefault(d => d.FriendlyName.StartsWith(name) || name.StartsWith(d.FriendlyName));
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
