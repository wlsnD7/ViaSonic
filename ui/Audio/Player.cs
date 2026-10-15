using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ViaTTS.Audio;

/// <summary>
/// 同时播放到虚拟麦克风和监听设备。语音一次一段（打断 / 排队）；音效可以叠加、循环；
/// 麦克风直通常驻在虚拟麦克风的混音流里。播放期间自动按住按键说话的键。
/// </summary>
public sealed class Player
{
    private sealed class Job(Clip clip, string tag)
    {
        public Clip Clip { get; } = clip;
        public string Tag { get; } = tag;
        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly Func<JsonObject> _cfg;
    private readonly BlockingCollection<Job> _queue = new();
    private readonly object _lock = new();
    private readonly BusPool _pool = new();
    private readonly Passthrough _pass;
    private readonly AppAudio _apps;
    private readonly Dictionary<string, Voice> _sounds = [];
    private readonly Dictionary<string, double> _startAt = []; // 没在播放时拖动进度条设的起点（秒）
    private Job? _current;
    private bool _playing;

    // 音量在界面线程更新，播放线程每次读取，拖动滑块立即生效
    private volatile float _micVol = 1, _monVol = 1, _soundVol = 1;
    private volatile Dictionary<string, (float Vol, bool Loop)> _items = [];

    private readonly MMDeviceEnumerator _en = new();
    private readonly DeviceWatcher _watcher;
    private readonly object _targetsLock = new();
    private List<(MMDevice Dev, Func<float> Vol)>? _targets;
    private string _targetsKey = "";

    private readonly object _pttLock = new();
    private int _pttCount;
    private string _pttKey = "";

    /// <summary>有没有在播放（语音或音效）。</summary>
    public event Action<bool>? PlayingChanged;
    /// <summary>正在播放的常用语变了，用 IsActive 查询。</summary>
    public event Action? ActiveChanged;

    public Player(Func<JsonObject> cfg)
    {
        _cfg = cfg;
        _pass = new Passthrough(_pool);
        _apps = new AppAudio(_pool);
        _watcher = new DeviceWatcher(DevicesChanged);
        _en.RegisterEndpointNotificationCallback(_watcher);
        OnConfig();
        new Thread(Worker) { IsBackground = true, Name = "player" }.Start();
    }

    private JsonObject C => _cfg();
    private string Str(string k) => C[k]?.GetValue<string>() ?? "";
    private bool Bool(string k) => C[k]?.GetValue<bool>() ?? false;
    private double Num(string k) => App.ToDouble(C[k]);

    /// <summary>配置变了：刷新音量和麦克风直通。</summary>
    public void OnConfig()
    {
        _micVol = (float)Num("mic_volume");
        _monVol = (float)Num("monitor_volume");
        _soundVol = (float)Num("sound_volume");
        _pass.Volume = (float)Num("passthrough_volume");
        var items = new Dictionary<string, (float, bool)>();
        foreach (var p in C["phrases"]?.AsArray().OfType<JsonObject>() ?? [])
            items[p["id"]?.GetValue<string>() ?? ""] = ((float)App.ToDouble(p["volume"]), p["loop"]?.GetValue<bool>() ?? false);
        _items = items;
        _pass.Set(Bool("mic_enabled") && Bool("mic_passthrough") ? (Str("mic_device"), Str("passthrough_device")) : null);
        var apps = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in C["app_audio_apps"]?.AsArray().OfType<JsonObject>() ?? [])
            if (a["exe"]?.GetValue<string>() is { Length: > 0 } exe) apps[exe] = (float)App.ToDouble(a["volume"]);
        _apps.Set(Bool("mic_enabled") && Bool("app_audio") ? Str("mic_device") : null, apps);
        PttSwitch();
        ThreadPool.QueueUserWorkItem(_ => Targets()); // 提前找好设备，第一次播放不用等
    }

    /// <summary>有没有在播放（语音或音效）。</summary>
    public bool Playing
    {
        get { lock (_lock) return _playing; }
    }

    /// <summary>应用声音的电平（0~1），见 AppAudio.TakeLevel。</summary>
    public float AppLevel(string exe) => _apps.TakeLevel(exe);

    public bool IsActive(string tag)
    {
        lock (_lock) return _sounds.ContainsKey(tag) || (_current != null && _current.Tag == tag && tag.Length > 0);
    }

    /// <summary>这一条正作为音效播放（音频文件或循环的文字）。</summary>
    public bool IsSound(string id)
    {
        lock (_lock) return _sounds.ContainsKey(id);
    }

    /// <summary>正在播放的音效的位置和总长（秒）；没在播放时为 null。</summary>
    public (double Pos, double Len)? Progress(string id)
    {
        lock (_lock)
            return _sounds.TryGetValue(id, out var v) ? ((double)v.Position / v.Clip.SampleRate, (double)v.Clip.Samples.Length / v.Clip.SampleRate) : null;
    }

    public void Seek(string id, double seconds)
    {
        lock (_lock) if (_sounds.TryGetValue(id, out var v)) v.Seek((int)(seconds * v.Clip.SampleRate));
    }

    /// <summary>下次播放这一条时从哪里开始（秒），播放时用掉。</summary>
    public void SetStart(string id, double seconds)
    {
        lock (_lock)
        {
            if (seconds > 0) _startAt[id] = seconds;
            else _startAt.Remove(id);
        }
    }

    public double StartOf(string id)
    {
        lock (_lock) return _startAt.TryGetValue(id, out var s) ? s : 0;
    }

    // ------------------------------------------------------------------ 语音
    public void Play(Clip clip, string tag = "")
    {
        if (Str("play_mode") == "interrupt") StopSpeech();
        _queue.Add(new Job(clip, tag));
        Notify();
    }

    public void StopSpeech()
    {
        lock (_lock)
        {
            while (_queue.TryTake(out var j)) j.Cts.Cancel();
            _current?.Cts.Cancel();
        }
    }

    /// <summary>停止全部：语音和音效。</summary>
    public void Stop()
    {
        StopSpeech();
        List<Voice> all;
        lock (_lock) all = [.. _sounds.Values];
        foreach (var v in all) v.Stop();
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
            Notify();
            try
            {
                var voice = new Voice(job.Clip, (int)Num("lead_silence_ms"), () => false, () => 1f, 0);
                if (PttDown(job.Cts.Token)) Start(voice);
                WaitHandle.WaitAny([voice.Done, job.Cts.Token.WaitHandle]);
                voice.Stop();
                PttUp();
            }
            catch (Exception e) { Log.Error(T("播放失败"), e); }
            finally
            {
                lock (_lock) _current = null;
                Notify();
            }
        }
    }

    // ------------------------------------------------------------------ 音效
    /// <summary>
    /// 播放常用语的音频文件（或循环的文字），和语音、其他音效叠加。同一条再次播放时先停掉旧的。音量、循环在播放中修改也立即生效。
    /// speech：文字合成的语音，不受「音频音量」影响。
    /// </summary>
    public void PlaySound(string id, Clip clip, bool speech = false)
    {
        int start;
        lock (_lock) start = _startAt.Remove(id, out var sec) ? (int)(sec * clip.SampleRate) : 0;
        var voice = new Voice(clip, (int)Num("lead_silence_ms"),
            () => _items.TryGetValue(id, out var it) && it.Loop,
            speech ? () => 1f : () => _soundVol * (_items.TryGetValue(id, out var it) ? it.Vol : 1f),
            Math.Clamp(start, 0, clip.Samples.Length));
        lock (_lock)
        {
            if (_sounds.TryGetValue(id, out var old)) old.Stop();
            _sounds[id] = voice;
        }
        Notify();
        _ = Task.Run(() =>
        {
            try
            {
                if (PttDown(voice.Token)) Start(voice);
                voice.Done.WaitOne();
                PttUp();
            }
            catch (Exception e) { Log.Error(T("播放失败"), e); }
            finally
            {
                lock (_lock) if (_sounds.TryGetValue(id, out var cur) && cur == voice) _sounds.Remove(id);
                Notify();
            }
        });
    }

    public void StopSound(string id)
    {
        lock (_lock) if (_sounds.TryGetValue(id, out var v)) v.Stop();
    }

    // ------------------------------------------------------------------ 输出
    private void Start(Voice voice)
    {
        var targets = Targets();
        voice.Begin(targets.Count);
        foreach (var (dev, vol) in targets)
            if (!_pool.Add(dev, voice.Stream(vol), voice.StreamEnded)) voice.StreamEnded();
    }

    /// <summary>输出设备。枚举设备很慢（有的电脑要 1 秒以上），所以缓存起来，设置或系统设备变化时再重新查找。</summary>
    private List<(MMDevice Dev, Func<float> Vol)> Targets()
    {
        var key = $"{Bool("mic_enabled")}|{Str("mic_device")}|{Bool("monitor_enabled")}|{Str("monitor_device")}";
        lock (_targetsLock)
        {
            if (_targets != null && key == _targetsKey) return _targets;
            var list = new List<(MMDevice, Func<float>)>();
            var played = new HashSet<string>();
            try
            {
                if (Bool("mic_enabled"))
                {
                    var name = Str("mic_device");
                    if (name.Length == 0) name = Devices.VirtualCable();
                    if (name.Length == 0) Log.Warn(T("找不到虚拟麦克风（未安装 VB-Cable）"));
                    else if (Devices.Find(_en, name) is { } d && played.Add(d.ID)) list.Add((d, () => _micVol));
                }
                if (Bool("monitor_enabled") && Devices.Find(_en, Str("monitor_device")) is { } m && played.Add(m.ID))
                    list.Add((m, () => _monVol));
            }
            catch (Exception e) { Log.Error(T("播放失败"), e); }
            _targets = list;
            _targetsKey = key;
            return list;
        }
    }

    private void DevicesChanged()
    {
        lock (_targetsLock) _targets = null;
    }

    private sealed class DeviceWatcher(Action changed) : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string pwstrDeviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => changed();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private void Notify()
    {
        bool now, changed;
        lock (_lock)
        {
            now = _current != null || _queue.Count > 0 || _sounds.Count > 0;
            changed = now != _playing;
            _playing = now;
        }
        ActiveChanged?.Invoke();
        if (changed) PlayingChanged?.Invoke(now);
    }

    /// <summary>按下按键说话的键；已经按着（别的声音在播）时不用再等。返回 false 表示等待期间被取消。</summary>
    private bool PttDown(CancellationToken token)
    {
        bool wait;
        lock (_pttLock)
        {
            wait = _pttCount++ == 0;
            if (wait)
            {
                _pttKey = PttKey();
                if (_pttKey.Length > 0) Ptt.Press(_pttKey, true);
                else wait = false;
            }
        }
        if (wait) token.WaitHandle.WaitOne((int)Num("ptt_delay_ms"));
        return !token.IsCancellationRequested;
    }

    /// <summary>要按的键；按键说话关着时为空。</summary>
    private string PttKey()
    {
        if (!Bool("ptt_enabled")) return "";
        try { return Ptt.Parse(Str("ptt_key")); }
        catch (Exception e) { Log.Warn(e.Message); return ""; }
    }

    /// <summary>播放途中开关按键说话：关掉立即松开，打开立即按下。</summary>
    private void PttSwitch()
    {
        lock (_pttLock)
        {
            if (_pttCount == 0) return;
            var key = PttKey();
            if (key == _pttKey) return;
            if (_pttKey.Length > 0) Ptt.Press(_pttKey, false);
            _pttKey = key;
            if (_pttKey.Length > 0) Ptt.Press(_pttKey, true);
        }
    }

    /// <summary>最后一个声音播完 100ms 后松开。</summary>
    private void PttUp() => _ = Task.Delay(100).ContinueWith(_ =>
    {
        lock (_pttLock)
            if (--_pttCount == 0 && _pttKey.Length > 0) Ptt.Press(_pttKey, false);
    });

    public void Dispose()
    {
        Stop();
        _pass.Dispose();
        _apps.Dispose();
        _pool.CloseAll();
        lock (_pttLock) if (_pttCount > 0 && _pttKey.Length > 0) Ptt.Press(_pttKey, false);
    }

    /// <summary>一次播放：在每个输出设备上各有一路流，全部读完（或被停止）时 Done。start：前置静音之后从第几个采样开始。</summary>
    private sealed class Voice(Clip clip, int leadMs, Func<bool> loop, Func<float> gain, int start)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEvent _done = new(false);
        private int _pending, _streams;
        private int _seekGen, _seekPos;
        private volatile int _position = -1;

        /// <summary>第一路流读到的位置（采样）。</summary>
        public int Position => _position < 0 ? Start : _position;

        /// <summary>每路流在下次读取时跳到这个位置。</summary>
        public void Seek(int pos)
        {
            Volatile.Write(ref _seekPos, Math.Clamp(pos, 0, clip.Samples.Length));
            Interlocked.Increment(ref _seekGen);
        }

        public WaitHandle Done => _done;
        public CancellationToken Token => _cts.Token;

        public void Stop()
        {
            _cts.Cancel();
            if (Volatile.Read(ref _pending) == 0) _done.Set();
        }

        public void Begin(int streams)
        {
            _pending = streams;
            if (streams == 0 || _cts.IsCancellationRequested) _done.Set();
        }

        public void StreamEnded()
        {
            if (Interlocked.Decrement(ref _pending) <= 0) _done.Set();
        }

        public ISampleProvider Stream(Func<float> deviceVol) => new Reader(this, deviceVol, Interlocked.Increment(ref _streams) == 1);

        private sealed class Reader(Voice v, Func<float> deviceVol, bool primary) : ISampleProvider
        {
            private int _pos = v.LeadSamples > 0 ? -v.LeadSamples : v.Start;
            private int _seekGen;
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(v.Clip.SampleRate, 1);

            public int Read(float[] buffer, int offset, int count)
            {
                if (v._cts.IsCancellationRequested) return 0;
                int gen = Volatile.Read(ref v._seekGen);
                if (gen != _seekGen)
                {
                    _seekGen = gen;
                    _pos = Volatile.Read(ref v._seekPos);
                }
                int n = Fill(buffer, offset, count, v.Gain() * deviceVol());
                if (primary && _pos >= 0) v._position = _pos;
                return n;
            }

            private int Fill(float[] buffer, int offset, int count, float g)
            {
                var s = v.Clip.Samples;
                int n = 0;
                while (n < count)
                {
                    if (_pos < 0)
                    {
                        int z = Math.Min(-_pos, count - n);
                        for (int i = 0; i < z; i++) buffer[offset + n + i] = 0;
                        n += z;
                        _pos += z;
                        if (_pos == 0) _pos = v.Start; // 前置静音之后从起点开始
                        continue;
                    }
                    if (_pos >= s.Length)
                    {
                        if (s.Length == 0 || !v.Loop()) break;
                        _pos = 0;
                    }
                    int k = Math.Min(s.Length - _pos, count - n);
                    for (int i = 0; i < k; i++) buffer[offset + n + i] = s[_pos + i] * g;
                    n += k;
                    _pos += k;
                }
                return n;
            }
        }

        public Clip Clip => clip;
        public Func<bool> Loop => loop;
        public Func<float> Gain => gain;
        public int LeadSamples => clip.SampleRate * Math.Max(0, leadMs) / 1000;
        public int Start => start;
    }
}
