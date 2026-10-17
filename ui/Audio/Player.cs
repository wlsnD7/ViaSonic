using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ViaSonic.Audio;

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
    private readonly HashSet<string> _speechSounds = []; // 作为音效播放的文字语音（循环的文字常用语）
    private Job? _current;
    private bool _playing;

    // 音量在界面线程更新，播放线程每次读取，拖动滑块立即生效
    private volatile float _micVol = 1, _monVol = 1, _soundVol = 1, _soundMonVol = 1;
    private volatile Dictionary<string, (float Vol, bool Loop)> _items = [];

    private readonly MMDeviceEnumerator _en = new();
    private readonly DeviceWatcher _watcher;
    private readonly object _targetsLock = new();
    private List<(MMDevice Dev, Func<float> Vol, bool Monitor)>? _targets;
    private string _targetsKey = "";

    private readonly object _pttLock = new();
    private int _pttCount;
    private string _pttKey = "";

    /// <summary>有没有在播放语音（朗读和文字常用语，音频文件不算）。</summary>
    public event Action<bool>? PlayingChanged;
    /// <summary>正在播放的常用语变了，用 IsActive 查询。</summary>
    public event Action? ActiveChanged;

    public Player(Func<JsonObject> cfg)
    {
        _cfg = cfg;
        _pass = new Passthrough(_pool, Rvc);
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
        _soundMonVol = (float)Num("sound_monitor_volume");
        _pass.Volume = (float)Num("passthrough_volume");
        var items = new Dictionary<string, (float, bool)>();
        foreach (var p in C["phrases"]?.AsArray().OfType<JsonObject>() ?? [])
            items[p["id"]?.GetValue<string>() ?? ""] = ((float)App.ToDouble(p["volume"]), p["loop"]?.GetValue<bool>() ?? false);
        _items = items;
        // 变声：麦克风效果立即生效；「监听麦克风」把变声后的麦克风也送到监听设备
        bool fx = Bool("fx_enabled");
        var effects = VoiceFx.Settings.Parse(C["fx_effects"] as JsonObject);
        _pass.Fx = fx && Bool("fx_mic") ? effects : VoiceFx.Settings.None;
        SpeechFx = fx && Bool("fx_speech") ? effects : VoiceFx.Settings.None;
        _pass.MonitorVolume = (float)Num("monitor_volume");
        bool pass = Bool("mic_enabled") && Bool("mic_passthrough");
        bool rvc = RvcConfig(pass && fx && Bool("fx_mic"));
        _pass.Set(pass
            ? new Passthrough.Target(Str("mic_device"), Str("passthrough_device"), Bool("mic_monitor") ? Str("monitor_device") : null, rvc)
            : null);
        var apps = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in C["app_audio_apps"]?.AsArray().OfType<JsonObject>() ?? [])
            if (a["exe"]?.GetValue<string>() is { Length: > 0 } exe) apps[exe] = (float)App.ToDouble(a["volume"]);
        _apps.Set(Bool("mic_enabled") && Bool("app_audio") ? Str("mic_device") : null, apps);
        PttSwitch();
        ThreadPool.QueueUserWorkItem(_ => Targets()); // 提前找好设备，第一次播放不用等
    }

    /// <summary>AI 变声（RVC）进程。</summary>
    public RvcHost Rvc { get; } = new();

    /// <summary>朗读语音的变声效果（关着时为 None）。</summary>
    public VoiceFx.Settings SpeechFx { get; private set; } = VoiceFx.Settings.None;

    private (string? Key, string? Home) _rvcHome = (null, null); // Key == null：还没找过
    private (string Model, string Index) _rvcIndex = ("", "");

    /// <summary>整合包目录（找过一次就缓存，配置变了再找）；没找到或是旧版时为 null。</summary>
    public string? RvcHome
    {
        get
        {
            var key = Str("rvc_home");
            if (_rvcHome.Key != key) _rvcHome = (key, RvcHost.FindHome(key));
            return _rvcHome.Home;
        }
    }

    /// <summary>按当前组合设置 RVC：参数立即生效，模型等变了才重启进程；用不到时停掉释放显存。返回要不要接到麦克风上。</summary>
    private bool RvcConfig(bool wanted)
    {
        var r = (C["fx_effects"] as JsonObject)?["rvc"] as JsonObject;
        var model = r?["model"]?.GetValue<string>() ?? "";
        Rvc.Live = new RvcHost.Params((float)App.ToDouble(r?["pitch"]), (float)App.ToDouble(r?["formant"]),
            (float)(App.ToDouble(r?["index"]) / 100), (float)(App.ToDouble(r?["rms"]) / 100), (float)App.ToDouble(r?["gate"]), (int)Num("rvc_f0"));
        var home = RvcHome;
        bool on = wanted && (r?["on"]?.GetValue<bool>() ?? false) && model.Length > 0 && RvcHost.IsHome(home) && File.Exists(model);
        if (on && _rvcIndex.Model != model + "|" + home) _rvcIndex = (model + "|" + home, RvcHost.FindIndex(model, home));
        Rvc.Want(on ? new RvcHost.Options(home!, model, _rvcIndex.Index, (int)Num("rvc_block_ms"),
            (int)Num("rvc_crossfade_ms"), (int)Num("rvc_extra_ms")) : null);
        return on;
    }

    /// <summary>有没有在播放语音（朗读和文字常用语，音频文件不算）。</summary>
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
                var voice = new Voice(job.Clip, (int)Num("lead_silence_ms"), () => false, _ => 1f, 0);
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
    /// <param name="ended">播完或被停止时调用：true＝自然播完。</param>
    /// <param name="fadeIn">开头淡入多少秒（播放列表用）。</param>
    /// <param name="fadeTail">结尾最后多少秒淡出（播放列表的交叉过渡用）。</param>
    /// <param name="speed">播放速度（变速不变调，播放列表用）；null＝原速。</param>
    public void PlaySound(string id, Clip clip, bool speech = false, Action<bool>? ended = null, double fadeIn = 0, double fadeTail = 0,
        Func<double>? speed = null)
    {
        int start;
        lock (_lock) start = _startAt.Remove(id, out var sec) ? (int)(sec * clip.SampleRate) : 0;
        // 音频文件：进虚拟麦克风和进监听分别用「音频音量」的两个滑块
        var voice = new Voice(clip, (int)Num("lead_silence_ms"),
            () => _items.TryGetValue(id, out var it) && it.Loop,
            speech ? _ => 1f : mon => (mon ? _soundMonVol : _soundVol) * (_items.TryGetValue(id, out var it) ? it.Vol : 1f),
            Math.Clamp(start, 0, clip.Samples.Length), speed)
        {
            FadeIn = (int)(fadeIn * clip.SampleRate),
            FadeTail = (int)(fadeTail * clip.SampleRate),
        };
        lock (_lock)
        {
            if (_sounds.TryGetValue(id, out var old)) old.Stop();
            _sounds[id] = voice;
            if (speech) _speechSounds.Add(id);
            else _speechSounds.Remove(id);
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
                lock (_lock)
                    if (_sounds.TryGetValue(id, out var cur) && cur == voice)
                    {
                        _sounds.Remove(id);
                        _speechSounds.Remove(id);
                    }
                Notify();
                ended?.Invoke(!voice.Token.IsCancellationRequested && !voice.Stopped);
            }
        });
    }

    public void StopSound(string id)
    {
        lock (_lock) if (_sounds.TryGetValue(id, out var v)) v.Stop();
    }

    /// <summary>用 seconds 秒淡出后停止（播放列表暂停、切歌，避免咔的一声）。</summary>
    public void FadeStopSound(string id, double seconds)
    {
        lock (_lock) if (_sounds.TryGetValue(id, out var v)) v.FadeStop((int)(seconds * v.Clip.SampleRate));
    }

    // ------------------------------------------------------------------ 输出
    private void Start(Voice voice)
    {
        var targets = Targets();
        voice.Begin(targets.Count);
        foreach (var (dev, vol, mon) in targets)
            if (!_pool.Add(dev, voice.Stream(vol, mon), voice.StreamEnded)) voice.StreamEnded();
    }

    /// <summary>输出设备。枚举设备很慢（有的电脑要 1 秒以上），所以缓存起来，设置或系统设备变化时再重新查找。</summary>
    private List<(MMDevice Dev, Func<float> Vol, bool Monitor)> Targets()
    {
        var key = $"{Bool("mic_enabled")}|{Str("mic_device")}|{Bool("monitor_enabled")}|{Str("monitor_device")}";
        lock (_targetsLock)
        {
            if (_targets != null && key == _targetsKey) return _targets;
            var list = new List<(MMDevice, Func<float>, bool)>();
            var played = new HashSet<string>();
            try
            {
                if (Bool("mic_enabled"))
                {
                    var name = Str("mic_device");
                    if (name.Length == 0) name = Devices.VirtualCable();
                    if (name.Length == 0) Log.Warn(T("找不到虚拟麦克风（未安装 VB-Cable）"));
                    else if (Devices.Find(_en, name) is { } d && played.Add(d.ID)) list.Add((d, () => _micVol, false));
                }
                if (Bool("monitor_enabled") && Devices.Find(_en, Str("monitor_device")) is { } m && played.Add(m.ID))
                    list.Add((m, () => _monVol, true));
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
            now = _current != null || _queue.Count > 0 || _speechSounds.Count > 0;
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
        Rvc.Dispose();
        _apps.Dispose();
        _pool.CloseAll();
        lock (_pttLock) if (_pttCount > 0 && _pttKey.Length > 0) Ptt.Press(_pttKey, false);
    }

    /// <summary>一次播放：在每个输出设备上各有一路流，全部读完（或被停止）时 Done。start：前置静音之后从第几个采样开始。</summary>
    /// <param name="gain">音量；参数为 true 时是送到监听设备的那一路。</param>
    /// <param name="speed">播放速度，null＝原速（不循环时才生效）。</param>
    private sealed class Voice(Clip clip, int leadMs, Func<bool> loop, Func<bool, float> gain, int start, Func<double>? speed = null)
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEvent _done = new(false);
        private int _pending, _streams;
        private int _seekGen, _seekPos;
        private volatile int _position = -1;
        private int _stopLen, _stopGen;

        /// <summary>开头淡入、结尾淡出的长度（采样），0＝不淡。</summary>
        public int FadeIn { get; init; }
        public int FadeTail { get; init; }
        /// <summary>被 FadeStop 停下（不算播完）。</summary>
        public bool Stopped { get; private set; }

        /// <summary>每路流在接下来 samples 个采样内淡出，然后结束。</summary>
        public void FadeStop(int samples)
        {
            Stopped = true;
            Volatile.Write(ref _stopLen, Math.Max(1, samples));
            Interlocked.Increment(ref _stopGen);
        }

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

        public ISampleProvider Stream(Func<float> deviceVol, bool monitor) =>
            new Reader(this, deviceVol, monitor, Interlocked.Increment(ref _streams) == 1);

        private sealed class Reader(Voice v, Func<float> deviceVol, bool monitor, bool primary) : ISampleProvider
        {
            private int _pos = v.LeadSamples > 0 ? -v.LeadSamples : v.Start;
            private int _seekGen, _stopGen;
            private int _played;        // 已经放出的采样数（开头淡入用）
            private int _stopLeft = -1; // 淡出停止还剩多少采样，-1＝没在停
            private int _ramp;          // 跳转、开关变速后的短淡入，避免咔声
            private readonly int _rampLen = v.Clip.SampleRate * 30 / 1000;
            private Stretch? _stretch;  // 变速时用
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(v.Clip.SampleRate, 1);

            public int Read(float[] buffer, int offset, int count)
            {
                if (v._cts.IsCancellationRequested) return 0;
                int gen = Volatile.Read(ref v._seekGen);
                if (gen != _seekGen)
                {
                    _seekGen = gen;
                    _pos = Volatile.Read(ref v._seekPos);
                    _stretch = null;
                    _ramp = _rampLen;
                }
                int sg = Volatile.Read(ref v._stopGen);
                if (sg != _stopGen)
                {
                    _stopGen = sg;
                    _stopLeft = Volatile.Read(ref v._stopLen);
                }
                if (_stopLeft == 0) return 0;
                double speed = v.Speed?.Invoke() ?? 1;
                bool stretch = Math.Abs(speed - 1) > 1e-3 && !v.Loop();
                if (!stretch && _stretch != null)
                {
                    _stretch = null; // 回到原速
                    _ramp = _rampLen;
                }
                float g = v.Gain(monitor) * deviceVol();
                int n = stretch ? FillStretched(buffer, offset, count, g, speed) : Fill(buffer, offset, count, g);
                if (primary && _pos >= 0) v._position = _pos;
                return n;
            }

            /// <summary>前置静音；返回写了多少个 0。</summary>
            private int Lead(float[] buffer, int offset, int count)
            {
                int z = Math.Min(-_pos, count);
                for (int i = 0; i < z; i++) buffer[offset + i] = 0;
                _pos += z;
                if (_pos == 0) _pos = v.Start; // 前置静音之后从起点开始
                return z;
            }

            /// <summary>淡入淡出的包络；淡出停止完了时 done。pos：源里的位置；结尾淡出按实际播放时间（除以速度）算。</summary>
            private float Envelope(int pos, double speed, bool looping, out bool done)
            {
                done = false;
                float e = 1;
                if (_played < v.FadeIn) e *= (float)_played / v.FadeIn;
                if (v.FadeTail > 0 && !looping)
                {
                    double rem = (v.Clip.Samples.Length - pos) / speed;
                    if (rem < v.FadeTail) e *= (float)(Math.Max(0, rem) / v.FadeTail);
                }
                if (_ramp > 0) e *= 1 - (float)_ramp-- / _rampLen;
                if (_stopLeft >= 0)
                {
                    if (_stopLeft == 0) { done = true; return 0; }
                    e *= (float)_stopLeft / Math.Max(1, v._stopLen);
                    _stopLeft--;
                }
                _played++;
                return e;
            }

            private int Fill(float[] buffer, int offset, int count, float g)
            {
                var s = v.Clip.Samples;
                int n = 0;
                while (n < count)
                {
                    if (_pos < 0)
                    {
                        n += Lead(buffer, offset + n, count - n);
                        continue;
                    }
                    if (_pos >= s.Length)
                    {
                        if (s.Length == 0 || !v.Loop()) break;
                        _pos = 0;
                    }
                    int k = Math.Min(s.Length - _pos, count - n);
                    if (v.FadeIn == 0 && v.FadeTail == 0 && _stopLeft < 0 && _ramp == 0)
                        for (int i = 0; i < k; i++) buffer[offset + n + i] = s[_pos + i] * g;
                    else
                    {
                        bool looping = v.FadeTail > 0 && v.Loop();
                        for (int i = 0; i < k; i++)
                        {
                            float e = Envelope(_pos + i, 1, looping, out bool done);
                            if (done)
                            {
                                _pos += i;
                                return n + i; // 淡出完了：这一路结束
                            }
                            buffer[offset + n + i] = s[_pos + i] * g * e;
                        }
                    }
                    n += k;
                    _pos += k;
                }
                return n;
            }

            private int FillStretched(float[] buffer, int offset, int count, float g, double speed)
            {
                int n = 0;
                while (n < count)
                {
                    if (_pos < 0)
                    {
                        n += Lead(buffer, offset + n, count - n);
                        continue;
                    }
                    if (_stretch == null)
                    {
                        _stretch = new Stretch(v.Clip, _pos);
                        if (_played > 0) _ramp = _rampLen; // 播放中切到变速
                    }
                    if (!_stretch.Next(speed, out float x, out int src))
                    {
                        _pos = v.Clip.Samples.Length;
                        break;
                    }
                    _pos = src;
                    float e = Envelope(src, speed, false, out bool done);
                    if (done) break;
                    buffer[offset + n++] = x * g * e;
                }
                return n;
            }
        }

        public Clip Clip => clip;
        public Func<bool> Loop => loop;
        public Func<bool, float> Gain => gain;
        public Func<double>? Speed => speed;
        public int LeadSamples => clip.SampleRate * Math.Max(0, leadMs) / 1000;
        public int Start => start;
    }

    /// <summary>
    /// 变速不变调（WSOLA）：每次从源里取一帧（40ms，汉宁窗，输出步长 20ms），帧起点按速度前进，
    /// 并在 ±10ms 内找和上一帧自然延续最像的位置，再重叠相加。
    /// </summary>
    private sealed class Stretch
    {
        private readonly float[] _x, _win, _acc, _out;
        private readonly int _n, _hs, _tol;
        private double _in;         // 下一帧的名义起点（源采样）
        private double _frameAt;    // 正在输出的这一帧的名义起点
        private int _prev = -1;     // 上一帧实际起点
        private int _outIdx, _outLen;

        public Stretch(Clip clip, int pos)
        {
            _x = clip.Samples;
            _hs = Math.Max(64, clip.SampleRate / 50);
            _n = _hs * 2;
            _tol = clip.SampleRate / 100;
            _win = new float[_n];
            for (int i = 0; i < _n; i++) _win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _n));
            _acc = new float[_n];
            _out = new float[_hs];
            _in = pos;
        }

        /// <summary>下一个输出采样和它对应的源位置；源读完时返回 false。</summary>
        public bool Next(double speed, out float y, out int src)
        {
            if (_outIdx >= _outLen && !Frame(speed))
            {
                y = 0;
                src = _x.Length;
                return false;
            }
            y = _out[_outIdx];
            src = Math.Min(_x.Length, (int)(_frameAt + _outIdx * speed));
            _outIdx++;
            return true;
        }

        private bool Frame(double speed)
        {
            int nominal = (int)_in;
            if (nominal >= _x.Length) return false;
            int start = _prev < 0 ? nominal : Best(nominal, _prev + _hs);
            for (int i = 0; i < _n; i++)
            {
                int j = start + i;
                _acc[i] += _win[i] * ((uint)j < (uint)_x.Length ? _x[j] : 0);
            }
            Array.Copy(_acc, _out, _hs);
            Array.Copy(_acc, _hs, _acc, 0, _hs);
            Array.Clear(_acc, _hs, _hs);
            _outIdx = 0;
            _outLen = _hs;
            _frameAt = _in;
            _prev = start;
            _in += _hs * speed;
            return true;
        }

        /// <summary>在 nominal ± tol 里找和 target 开头最相关的起点：先隔点粗搜，再在附近细搜。</summary>
        private int Best(int nominal, int target)
        {
            int lo = Math.Max(0, nominal - _tol), hi = nominal + _tol;
            if (target + _hs > _x.Length || hi + _hs > _x.Length || lo > hi) return nominal;
            int best = nominal;
            float bestDot = float.NegativeInfinity;
            for (int c = lo; c <= hi; c += 2)
            {
                float d = Dot(c, target, 4);
                if (d > bestDot) { bestDot = d; best = c; }
            }
            int center = best;
            bestDot = float.NegativeInfinity;
            for (int c = Math.Max(lo, center - 2); c <= Math.Min(hi, center + 2); c++)
            {
                float d = Dot(c, target, 1);
                if (d > bestDot) { bestDot = d; best = c; }
            }
            return best;
        }

        private float Dot(int a, int b, int step)
        {
            float s = 0;
            for (int i = 0; i < _hs; i += step) s += _x[a + i] * _x[b + i];
            return s;
        }
    }
}
