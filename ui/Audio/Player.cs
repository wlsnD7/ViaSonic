using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ViaTTS.Audio;

/// <summary>同时播放到虚拟麦克风和监听设备；支持打断 / 排队、立即停止、开头补静音、按键说话。</summary>
public sealed class Player
{
    private sealed class Job(Clip clip)
    {
        public Clip Clip { get; } = clip;
        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly Func<JsonObject> _cfg;
    private readonly BlockingCollection<Job> _queue = new();
    private readonly object _lock = new();
    private Job? _current;

    public event Action<bool>? PlayingChanged;

    public Player(Func<JsonObject> cfg)
    {
        _cfg = cfg;
        new Thread(Worker) { IsBackground = true, Name = "player" }.Start();
    }

    public bool Busy => _current != null || _queue.Count > 0;

    private JsonObject C => _cfg();
    private string Str(string k) => C[k]?.GetValue<string>() ?? "";
    private bool Bool(string k) => C[k]?.GetValue<bool>() ?? false;
    private double Num(string k) => App.ToDouble(C[k]);

    public void Play(Clip clip)
    {
        clip = clip.WithLeadSilence((int)Num("lead_silence_ms"));
        if (Str("play_mode") == "interrupt") Stop();
        _queue.Add(new Job(clip));
    }

    public void Stop()
    {
        lock (_lock)
        {
            while (_queue.TryTake(out var j)) j.Cts.Cancel();
            _current?.Cts.Cancel();
        }
    }

    private List<(string Name, float Volume)> Targets()
    {
        var list = new List<(string, float)>();
        if (Bool("mic_enabled"))
        {
            var name = Str("mic_device");
            if (name.Length == 0) name = Devices.VirtualCable();
            if (name.Length == 0) Log.Warn(T("找不到虚拟麦克风（未安装 VB-Cable）"));
            else list.Add((name, (float)Num("mic_volume")));
        }
        if (Bool("monitor_enabled")) list.Add((Str("monitor_device"), (float)Num("monitor_volume")));
        return list;
    }

    private void Worker()
    {
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            lock (_lock)
            {
                if (job.Cts.IsCancellationRequested) continue;
                _current = job;
            }
            PlayingChanged?.Invoke(true);
            string ptt = "";
            try { ptt = Ptt.Parse(Str("ptt_key")); } catch (Exception e) { Log.Warn(e.Message); }
            try
            {
                if (ptt.Length > 0)
                {
                    Ptt.Press(ptt, true);
                    job.Cts.Token.WaitHandle.WaitOne((int)Num("ptt_delay_ms"));
                }
                using var en = new MMDeviceEnumerator();
                var played = new HashSet<string>();
                var tasks = new List<Task>();
                foreach (var (name, vol) in Targets())
                {
                    var dev = Devices.Find(en, name);
                    if (dev == null || !played.Add(dev.ID)) continue;
                    tasks.Add(Task.Run(() => PlayOn(dev, vol, job.Clip, job.Cts.Token)));
                }
                Task.WaitAll([.. tasks]);
                if (ptt.Length > 0) job.Cts.Token.WaitHandle.WaitOne(100);
            }
            catch (Exception e) { Log.Error(T("播放失败"), e); }
            finally
            {
                if (ptt.Length > 0) Ptt.Press(ptt, false);
                lock (_lock) _current = null;
                PlayingChanged?.Invoke(_queue.Count > 0);
            }
        }
    }

    private static void PlayOn(MMDevice dev, float volume, Clip clip, CancellationToken token)
    {
        try
        {
            var mix = dev.AudioClient.MixFormat;
            ISampleProvider sp = new ClipProvider(clip);
            if (clip.SampleRate != mix.SampleRate) sp = new WdlResamplingSampleProvider(sp, mix.SampleRate);
            sp = new SpreadChannels(sp, mix.Channels);
            sp = new VolumeSampleProvider(sp) { Volume = volume };

            using var output = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
            var done = new ManualResetEventSlim();
            output.PlaybackStopped += (_, _) => done.Set();
            output.Init(sp);
            output.Play();
            WaitHandle.WaitAny([done.WaitHandle, token.WaitHandle]);
            if (token.IsCancellationRequested) output.Stop();
        }
        catch (Exception e)
        {
            Log.Error(T("播放到「{0}」失败：{1}", dev.FriendlyName, e.Message));
        }
    }

    private sealed class ClipProvider(Clip clip) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, clip.Samples.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(clip.Samples, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    /// <summary>单声道铺到设备的所有声道，顺便限幅。</summary>
    private sealed class SpreadChannels(ISampleProvider src, int channels) : ISampleProvider
    {
        private float[] _mono = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            int n = src.Read(_mono, 0, frames);
            for (int i = 0; i < n; i++)
            {
                float v = Math.Clamp(_mono[i], -1f, 1f);
                for (int c = 0; c < channels; c++) buffer[offset + i * channels + c] = v;
            }
            return n * channels;
        }
    }
}
