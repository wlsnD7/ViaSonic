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
    private sealed class Job(Clip clip, string tag, Action? ended)
    {
        private Action? _ended = ended;
        public Clip Clip { get; } = clip;
        public string Tag { get; } = tag;
        public CancellationTokenSource Cts { get; } = new();
        /// <summary>播完、被停止或被打断时调用一次。</summary>
        public void End() => Interlocked.Exchange(ref _ended, null)?.Invoke();
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

    private readonly PttHold _ptt;

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
        _ptt = new PttHold(PttKey, () => (int)C.Num("ptt_delay_ms"));
        OnConfig();
        new Thread(Worker) { IsBackground = true, Name = "player" }.Start();
    }

    private JsonObject C => _cfg();

    /// <summary>配置变了：刷新音量和麦克风直通。</summary>
    public void OnConfig()
    {
        _micVol = (float)C.Num("mic_volume");
        _monVol = (float)C.Num("monitor_volume");
        _soundVol = (float)C.Num("sound_volume");
        _soundMonVol = (float)C.Num("sound_monitor_volume");
        _pass.Volume = (float)C.Num("passthrough_volume");
        var items = new Dictionary<string, (float, bool)>();
        foreach (var p in C["phrases"]?.AsArray().OfType<JsonObject>() ?? [])
            items[p.Str("id")] = ((float)p.Num("volume"), p.Bool("loop"));
        _items = items;
        // 变声：麦克风效果立即生效；「监听麦克风」把变声后的麦克风也送到监听设备
        bool fx = C.Bool("fx_enabled");
        var effects = VoiceFx.Settings.Parse(C["fx_effects"] as JsonObject);
        _pass.Fx = fx && C.Bool("fx_mic") ? effects : VoiceFx.Settings.None;
        SpeechFx = fx && C.Bool("fx_speech") ? effects : VoiceFx.Settings.None;
        _pass.MonitorVolume = (float)C.Num("monitor_volume");
        bool pass = C.Bool("mic_enabled") && C.Bool("mic_passthrough");
        bool rvc = Rvc.Configure(C, pass && fx && C.Bool("fx_mic"));
        _pass.Set(pass
            ? new Passthrough.Target(C.Str("mic_device"), C.Str("passthrough_device"), C.Bool("mic_monitor") ? C.Str("monitor_device") : null, rvc)
            : null);
        var apps = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in C["app_audio_apps"]?.AsArray().OfType<JsonObject>() ?? [])
            if (a.Str("exe") is { Length: > 0 } exe) apps[exe] = (float)a.Num("volume");
        _apps.Set(C.Bool("mic_enabled") && C.Bool("app_audio") ? C.Str("mic_device") : null, apps);
        _ptt.Refresh();
        ThreadPool.QueueUserWorkItem(_ => Targets()); // 提前找好设备，第一次播放不用等
    }

    /// <summary>AI 变声（RVC）进程。</summary>
    public RvcHost Rvc { get; } = new();

    /// <summary>朗读语音的变声效果（关着时为 None）。</summary>
    public VoiceFx.Settings SpeechFx { get; private set; } = VoiceFx.Settings.None;

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
    /// <param name="ended">播完、被停止或被打断时调用（播放线程）。</param>
    public void Play(Clip clip, string tag = "", Action? ended = null)
    {
        if (C.Str("play_mode") == "interrupt") StopSpeech();
        _queue.Add(new Job(clip, tag, ended));
        Notify();
    }

    public void StopSpeech()
    {
        lock (_lock)
        {
            while (_queue.TryTake(out var j))
            {
                j.Cts.Cancel();
                j.End();
            }
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
                if (job.Cts.IsCancellationRequested)
                {
                    job.End();
                    continue;
                }
                _current = job;
            }
            Notify();
            try
            {
                var voice = new Voice(job.Clip, (int)C.Num("lead_silence_ms"), () => false, _ => 1f, 0);
                if (_ptt.Down(job.Cts.Token)) Start(voice);
                WaitHandle.WaitAny([voice.Done, job.Cts.Token.WaitHandle]);
                voice.Stop();
                _ptt.Up();
            }
            catch (Exception e) { Log.Error(T("播放失败"), e); }
            finally
            {
                lock (_lock) _current = null;
                Notify();
                job.End();
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
        var voice = new Voice(clip, (int)C.Num("lead_silence_ms"),
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
                if (_ptt.Down(voice.Token)) Start(voice);
                voice.Done.WaitOne();
                _ptt.Up();
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
            if (!_pool.Add(dev, voice.Stream(vol, mon), voice.StreamEnded, warmUp: true)) voice.StreamEnded();
    }

    /// <summary>输出设备。枚举设备很慢（有的电脑要 1 秒以上），所以缓存起来，设置或系统设备变化时再重新查找。</summary>
    private List<(MMDevice Dev, Func<float> Vol, bool Monitor)> Targets()
    {
        var key = $"{C.Bool("mic_enabled")}|{C.Str("mic_device")}|{C.Bool("monitor_enabled")}|{C.Str("monitor_device")}";
        lock (_targetsLock)
        {
            if (_targets != null && key == _targetsKey) return _targets;
            var list = new List<(MMDevice, Func<float>, bool)>();
            var played = new HashSet<string>();
            try
            {
                if (C.Bool("mic_enabled"))
                {
                    var name = C.Str("mic_device");
                    if (name.Length == 0) name = Devices.VirtualCable();
                    if (name.Length == 0) Log.Warn(T("找不到虚拟麦克风（未安装 VB-Cable 或 Voicemeeter）"));
                    else if (Devices.Find(_en, name) is { } d && played.Add(d.ID)) list.Add((d, () => _micVol, false));
                }
                if (C.Bool("monitor_enabled") && Devices.Find(_en, C.Str("monitor_device")) is { } m && played.Add(m.ID))
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

    /// <summary>要按的键；按键说话关着时为空。</summary>
    private string PttKey()
    {
        if (!C.Bool("ptt_enabled")) return "";
        try { return Ptt.Parse(C.Str("ptt_key")); }
        catch (Exception e) { Log.Warn(e.Message); return ""; }
    }

    public void Dispose()
    {
        Stop();
        _pass.Dispose();
        Rvc.Dispose();
        _apps.Dispose();
        _pool.CloseAll();
        _ptt.Release();
    }
}
