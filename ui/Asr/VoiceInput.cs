using System.Text.Json.Nodes;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using SherpaOnnx;
using ViaSonic.Audio;

namespace ViaSonic.Asr;

/// <summary>
/// 语音输入：录麦克风（和麦克风直通同一个设备，但只能开一个），识别成文字后用当前音色朗读。
/// 按住快捷键：按下开始录，松开后识别；持续检测：Silero VAD 判断一句话说完就识别，朗读播放期间不听。
/// 麦克风一直开着（按住模式留 0.3 秒预录，按下快捷键前的第一个字不会被切掉）。
/// </summary>
public sealed class VoiceInput : IDisposable
{
    public enum Phase { Off, Ready, Listening, Recognizing }

    private const int PreRoll = Pcm.Rate * 3 / 10, MinSamples = Pcm.Rate * 3 / 10, MaxSamples = Pcm.Rate * 60;

    private readonly Func<JsonObject> _cfg;
    private readonly Speaker _speaker;
    private readonly object _lock = new();
    private readonly Timer _retry;
    private readonly Channel<float[]> _segments = Channel.CreateUnbounded<float[]>();
    private (string Mic, bool Auto)? _want;
    private Capture? _cap;
    private string _lastCapError = "";

    // 以下只在录音线程里读写（_holding 由快捷键线程设置）
    private readonly float[] _pre = new float[PreRoll];
    private int _preHead;
    private volatile bool _holding;
    private List<float>? _hold;
    private volatile bool _speech;

    private volatile bool _playing;
    private long _quietUntil;

    public LocalAsr Local { get; } = new();
    /// <summary>麦克风打不开的原因。</summary>
    public string MicError => _lastCapError;
    public Dictionary<string, IRecognizer> Recognizers { get; }
    public Phase State { get; private set; } = Phase.Off;
    /// <summary>上一句识别出的文字，或者没识别到 / 出错的提示。</summary>
    public string LastText { get; private set; } = "";
    /// <summary>模型下载进度 0~1，没在下载时为 null。</summary>
    public double? Download { get; private set; }
    /// <summary>上次下载失败的原因。</summary>
    public string DownloadError { get; private set; } = "";
    private CancellationTokenSource? _downloadCts;

    /// <summary>状态有变化（可能在任意线程触发）。</summary>
    public event Action? Changed;

    public VoiceInput(Func<JsonObject> cfg, Speaker speaker, Player player)
    {
        _cfg = cfg;
        _speaker = speaker;
        Recognizers = new()
        {
            ["local"] = Local, ["azure"] = new AzureAsr(), ["bailian"] = new BailianAsr(), ["xunfei"] = new XunfeiAsr(), ["custom"] = new CustomAsr(),
        };
        Local.Changed += () => Changed?.Invoke();
        player.PlayingChanged += p =>
        {
            _playing = p;
            // 播完后再等一会儿：监听设备的余音还可能被麦克风收进去
            if (!p) Interlocked.Exchange(ref _quietUntil, Environment.TickCount64 + 400);
        };
        _retry = new Timer(_ => Reconcile(), null, 3000, 3000);
        _ = Task.Run(WorkerAsync);
    }

    private JsonObject C => _cfg();
    public IRecognizer Current => Recognizers.TryGetValue(C.Str("stt_engine", "local"), out var r) ? r : Local;
    private bool Auto => C.Str("stt_mode") == "auto";

    /// <summary>
    /// 语音输入和麦克风直通只能开一个：打开语音输入时关掉直通，关掉语音输入时恢复原来的直通；打开直通时关掉语音输入。
    /// 返回 true 表示改了配置。
    /// </summary>
    public static bool Exclusive(JsonObject c, string key)
    {
        if (key == "stt_enabled")
        {
            if (c.Bool("stt_enabled") && c.Bool("mic_passthrough"))
            {
                c["mic_passthrough"] = false;
                c["stt_pass_restore"] = true;
                return true;
            }
            if (!c.Bool("stt_enabled") && c.Bool("stt_pass_restore"))
            {
                c["stt_pass_restore"] = false;
                c["mic_passthrough"] = true;
                return true;
            }
        }
        else if (key == "mic_passthrough" && c.Bool("mic_passthrough") && c.Bool("stt_enabled"))
        {
            c["stt_enabled"] = false;
            c["stt_pass_restore"] = false;
            return true;
        }
        return false;
    }

    /// <summary>配置变了：开关麦克风、加载或释放本地模型。</summary>
    public void OnConfig()
    {
        bool on = C.Bool("stt_enabled");
        (string, bool)? want = on ? (C.Str("passthrough_device"), Auto) : null;
        if (on && C.Str("stt_engine", "local") == "local") Local.EnsureLoaded();
        else Local.Unload();
        lock (_lock)
        {
            if (_want == want) return;
            _want = want;
            _lastCapError = "";
            _cap?.Stop();
            _cap = null;
            _holding = false;
            _hold = null;
        }
        SetState(on ? Phase.Ready : Phase.Off);
        ThreadPool.QueueUserWorkItem(_ => Reconcile());
    }

    private void Reconcile()
    {
        lock (_lock)
        {
            if (_want is not { } w || _cap is { Alive: true }) return;
            _cap = null;
            var name = w.Mic;
            try
            {
                using var en = new MMDeviceEnumerator();
                var mic = Devices.FindInput(en, w.Mic);
                if (mic == null || Devices.IsVirtual(mic.FriendlyName)) return;
                name = mic.FriendlyName;
                _speech = false;
                _cap = new Capture(mic, this, w.Auto ? new SpeechSplitter() : null);
                if (_lastCapError.Length > 0)
                {
                    _lastCapError = "";
                    Changed?.Invoke();
                }
            }
            catch (Exception e)
            {
                var msg = T("无法打开麦克风「{0}」：{1}", name.Length > 0 ? name : T("默认"), e.Message);
                if (msg != _lastCapError)
                {
                    Log.Error(msg);
                    _lastCapError = msg;
                    Changed?.Invoke();
                }
            }
        }
    }

    // ------------------------------------------------------------------ 按住快捷键
    /// <summary>按下了「按住语音输入」：录到 vk 松开为止。在快捷键线程调用。</summary>
    public void Hold(int vk)
    {
        if (!C.Bool("stt_enabled") || Auto || _holding) return;
        lock (_lock)
        {
            if (_cap == null) return;
            var buf = new List<float>(Pcm.Rate * 5);
            lock (_pre)
                for (int i = 0; i < PreRoll; i++) buf.Add(_pre[(_preHead + i) % PreRoll]);
            _hold = buf;
            _holding = true;
        }
        SetState(Phase.Listening);
        _ = Task.Run(async () =>
        {
            var start = Environment.TickCount64;
            while (_holding && Native.IsDown(vk) && Environment.TickCount64 - start < 60_000) await Task.Delay(20);
            Release();
        });
    }

    private void Release()
    {
        List<float>? buf;
        lock (_lock)
        {
            // 按满 60 秒时录音线程已经把 _holding 清掉了，录到的还在 _hold 里
            _holding = false;
            buf = _hold;
            _hold = null;
        }
        if (buf == null) return;
        if (buf.Count - PreRoll >= MinSamples) _segments.Writer.TryWrite([.. buf]);
        else SetState(Phase.Ready);
    }

    // ------------------------------------------------------------------ 录音线程
    /// <summary>重采样到 16kHz 单声道后的一块录音。</summary>
    private void OnSamples(Capture cap, float[] s, int n)
    {
        lock (_pre)
            for (int i = 0; i < n; i++)
            {
                _pre[_preHead] = s[i];
                _preHead = (_preHead + 1) % PreRoll;
            }
        if (_holding)
        {
            lock (_lock)
                if (_hold != null)
                {
                    for (int i = 0; i < n; i++) _hold.Add(s[i]);
                    if (_hold.Count >= MaxSamples) _holding = false; // 轮询线程看到后交给识别
                }
            return;
        }
        if (cap.Vad is not { } vad) return;
        if (_playing || Environment.TickCount64 < Interlocked.Read(ref _quietUntil))
        {
            // 朗读播放期间不听，免得把自己的朗读又识别一遍
            if (_speech)
            {
                vad.Reset();
                _speech = false;
                SetState(Phase.Ready);
            }
            return;
        }
        foreach (var seg in vad.Feed(s.AsSpan(0, n)))
            if (seg.Length >= MinSamples) _segments.Writer.TryWrite(seg);
        bool speech = vad.Speech;
        if (speech != _speech)
        {
            _speech = speech;
            if (speech) SetState(Phase.Listening);
            else if (State == Phase.Listening) SetState(Phase.Ready);
        }
    }

    // ------------------------------------------------------------------ 识别
    private async Task WorkerAsync()
    {
        await foreach (var seg in _segments.Reader.ReadAllAsync())
        {
            var c = C;
            if (!c.Bool("stt_enabled")) continue;
            SetState(Phase.Recognizing);
            var rec = Current;
            string text;
            try
            {
                if (rec.Missing(c) is { Length: > 0 } missing) throw new AsrException(missing);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                text = Clean(await rec.RecognizeAsync(seg, c, timeout.Token));
                LastText = text.Length > 0 ? text : T("没有识别到文字");
            }
            catch (Exception e)
            {
                text = "";
                LastText = e is AsrException ? e.Message : T("识别失败：{0}", e.Message);
                Log.Error(T("识别失败：{0}", e.Message));
            }
            if (text.Length > 0) _speaker.Speak(text);
            SetState(c.Bool("stt_enabled") ? _speech || _holding ? Phase.Listening : Phase.Ready : Phase.Off);
        }
    }

    /// <summary>去掉首尾空白；只有标点没有字时当作没识别到。</summary>
    internal static string Clean(string text)
    {
        text = text.Trim();
        return text.Any(char.IsLetterOrDigit) ? text : "";
    }

    private void SetState(Phase p)
    {
        State = p;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ 模型下载
    public async Task DownloadModelAsync()
    {
        if (Download != null) return;
        _downloadCts = new CancellationTokenSource();
        Download = 0;
        DownloadError = "";
        Changed?.Invoke();
        var last = 0.0;
        var progress = new Progress<double>(p =>
        {
            Download = p;
            if (p - last >= 0.01) { last = p; Changed?.Invoke(); }
        });
        try
        {
            await AsrModel.DownloadAsync(progress, _downloadCts.Token);
            Log.Info(T("语音识别模型下载完成"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DownloadError = T("下载失败：{0}", e.Message);
            Log.Error(DownloadError);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Download = null;
            _downloadCts = null;
        }
        OnConfig(); // 下完后加载
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _retry.Dispose();
        _downloadCts?.Cancel();
        lock (_lock)
        {
            _want = null;
            _cap?.Stop();
            _cap = null;
        }
        Local.Dispose();
    }

    /// <summary>麦克风录音 → 单声道 → 16kHz，交给 OnSamples。VAD 跟着录音走，录音线程停了才释放。</summary>
    private sealed class Capture
    {
        private readonly WasapiCapture _cap;
        private volatile bool _alive = true;

        public bool Alive => _alive;
        /// <summary>持续检测用；按住模式为 null。</summary>
        public SpeechSplitter? Vad { get; }

        public Capture(MMDevice mic, VoiceInput owner, SpeechSplitter? vad)
        {
            Vad = vad;
            _cap = new WasapiCapture(mic, true, 20);
            var to16k = new To16k(_cap.WaveFormat);
            _cap.DataAvailable += (_, e) =>
            {
                if (!_alive) return;
                int n = to16k.Process(e.Buffer, e.BytesRecorded, out var pcm);
                if (n > 0) owner.OnSamples(this, pcm, n);
            };
            _cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Error(T("无法打开麦克风「{0}」：{1}", mic.FriendlyName, e.Exception.Message));
                Stop();
            };
            _cap.StartRecording();
        }

        public void Stop()
        {
            if (!_alive) return;
            _alive = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _cap.StopRecording(); } catch { }
                try { _cap.Dispose(); } catch { }
                Vad?.Dispose();
            });
        }
    }
}

/// <summary>录音数据（任意采样率、声道、格式）→ 16kHz 单声道 float。</summary>
internal sealed class To16k
{
    private readonly WaveFormat _wf;
    private readonly bool _isFloat;
    private readonly WdlResampler _rs = new();
    private float[] _out = [];

    public To16k(WaveFormat wf)
    {
        _wf = wf;
        _isFloat = Ring.IsFloat(wf);
        _rs.SetMode(true, 2, false);
        _rs.SetFilterParms();
        _rs.SetFeedMode(true);
        _rs.SetRates(wf.SampleRate, Pcm.Rate);
    }

    /// <summary>返回输出的样本数，结果在 pcm 的开头（下次调用会覆盖）。</summary>
    public int Process(byte[] data, int bytes, out float[] pcm)
    {
        pcm = _out;
        int frames = bytes / _wf.BlockAlign;
        if (frames == 0) return 0;
        int need = _rs.ResamplePrepare(frames, 1, out var inBuf, out var inOff);
        for (int f = 0; f < need; f++) inBuf[inOff + f] = Ring.MonoAt(data, f, _wf, _isFloat);
        int max = frames * Pcm.Rate / _wf.SampleRate + 16;
        if (_out.Length < max) _out = new float[max];
        pcm = _out;
        return _rs.ResampleOut(_out, 0, need, max, 1);
    }
}

/// <summary>Silero VAD 分句：喂 16kHz 录音，吐出说完的句子（停顿 0.6 秒算说完，一句最长 20 秒）。</summary>
internal sealed class SpeechSplitter : IDisposable
{
    private const int Window = 512;
    private readonly VoiceActivityDetector _vad;
    private readonly List<float> _pending = [];

    /// <summary>正在说话（一句还没说完）。</summary>
    public bool Speech { get; private set; }

    public SpeechSplitter()
    {
        var cfg = new VadModelConfig();
        cfg.SileroVad.Model = NativePath.Short(Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx"));
        cfg.SileroVad.MinSilenceDuration = 0.6f;
        cfg.SileroVad.MinSpeechDuration = 0.25f;
        cfg.SileroVad.MaxSpeechDuration = 20f;
        cfg.SampleRate = Pcm.Rate;
        _vad = new VoiceActivityDetector(cfg, 30);
    }

    public List<float[]> Feed(ReadOnlySpan<float> s)
    {
        foreach (var x in s) _pending.Add(x);
        int off = 0;
        while (_pending.Count - off >= Window)
        {
            _vad.AcceptWaveform(_pending.GetRange(off, Window).ToArray());
            off += Window;
        }
        _pending.RemoveRange(0, off);
        var done = new List<float[]>();
        while (!_vad.IsEmpty())
        {
            done.Add(_vad.Front().Samples);
            _vad.Pop();
        }
        Speech = _vad.IsSpeechDetected();
        return done;
    }

    /// <summary>丢掉没说完的半句。</summary>
    public void Reset()
    {
        _vad.Clear();
        _vad.Reset();
        _pending.Clear();
        Speech = false;
    }

    public void Dispose() => _vad.Dispose();
}
