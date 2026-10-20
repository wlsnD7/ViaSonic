using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ViaSonic.Audio;

/// <summary>
/// 麦克风直通：录真实麦克风，经过变声效果，作为一路常驻输入加到虚拟麦克风的混音流里；
/// 打开「监听麦克风」时，变声后的声音同时送到监听设备。设备断开后定时重试。
/// </summary>
public sealed class Passthrough
{
    /// <summary>Monitor：监听设备名（空＝默认），null＝不监听。Rvc：经过 AI 变声。</summary>
    public readonly record struct Target(string Cable, string Mic, string? Monitor, bool Rvc);

    private readonly BusPool _pool;
    private readonly RvcHost _rvc;
    private readonly Timer _retry;
    private readonly object _lock = new();
    private Target? _want;
    private Session? _cur;
    private string _lastError = "";
    private volatile VoiceFx.Settings _fx = VoiceFx.Settings.None;

    public volatile float Volume = 1f;
    public volatile float MonitorVolume = 1f;

    public Passthrough(BusPool pool, RvcHost rvc)
    {
        _pool = pool;
        _rvc = rvc;
        _retry = new Timer(_ => Reconcile(), null, 3000, 3000);
    }

    /// <summary>麦克风的变声效果，立即生效。</summary>
    public VoiceFx.Settings Fx
    {
        get => _fx;
        set
        {
            _fx = value;
            lock (_lock) _cur?.SetFx(value);
        }
    }

    /// <summary>设置目标：虚拟麦克风设备名（空＝自动）、麦克风设备名（空＝默认）、监听设备；null 表示关闭。</summary>
    public void Set(Target? want)
    {
        lock (_lock)
        {
            if (_want == want) return;
            _want = want;
            _lastError = "";
            _cur?.Stop();
            _cur = null;
        }
        ThreadPool.QueueUserWorkItem(_ => Reconcile());
    }

    private void Reconcile()
    {
        lock (_lock)
        {
            if (_want is not { } w || _cur is { Alive: true }) return;
            _cur = null;
            string micName = w.Mic;
            try
            {
                using var en = new MMDeviceEnumerator();
                var cableName = w.Cable.Length > 0 ? w.Cable : Devices.VirtualCable();
                var cable = cableName.Length > 0 ? Devices.Find(en, cableName) : null;
                if (cable == null) return;
                var mic = Devices.FindInput(en, w.Mic);
                if (mic == null) return;
                micName = mic.FriendlyName;
                if (Devices.IsVirtual(micName)) return; // 录虚拟麦克风自己会形成回环
                var monitor = w.Monitor != null ? Devices.Find(en, w.Monitor) : null;
                if (monitor?.ID == cable.ID) monitor = null;
                var s = new Session(mic, this, monitor != null, w.Rvc ? _rvc : null);
                s.SetFx(_fx);
                if (!_pool.Add(cable, s.Source, s.Stop))
                {
                    s.Stop();
                    return;
                }
                if (monitor != null && s.Monitor != null) _pool.Add(monitor, s.Monitor, () => { });
                _cur = s;
                _lastError = "";
            }
            catch (Exception e)
            {
                var msg = T("无法打开麦克风「{0}」：{1}", micName.Length > 0 ? micName : T("默认"), e.Message);
                if (msg != _lastError) Log.Error(msg);
                _lastError = msg;
            }
        }
    }

    public void Dispose()
    {
        _retry.Dispose();
        Set(null);
    }

    private sealed class Session
    {
        private readonly WasapiCapture _cap;
        private readonly Ring _ring;
        private readonly Processed _source;
        private readonly RvcStream? _rvcStream;
        private volatile bool _alive = true;

        public ISampleProvider Source => _source;
        /// <summary>变声后的声音给监听设备；不监听时为 null。</summary>
        public Ring? Monitor { get; }
        public bool Alive => _alive;

        public Session(MMDevice mic, Passthrough owner, bool monitor, RvcHost? rvc)
        {
            _cap = new WasapiCapture(mic, true, 20);
            var wf = _cap.WaveFormat;
            _ring = new Ring(wf.SampleRate, () => owner.Volume);
            // AI 变声：先重采样到 48kHz 送进 RVC，后面的效果也在 48kHz 上做
            ISampleProvider src = _ring;
            if (rvc != null)
            {
                if (src.WaveFormat.SampleRate != RvcHost.Rate) src = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(src, RvcHost.Rate);
                src = _rvcStream = new RvcStream(src, rvc);
            }
            int rate = src.WaveFormat.SampleRate;
            if (monitor) Monitor = new Ring(rate, () => owner.MonitorVolume);
            _source = new Processed(src, new VoiceFx(rate), Monitor);
            _cap.DataAvailable += (_, e) => _ring.Write(e.Buffer, e.BytesRecorded, wf);
            _cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Error(T("无法打开麦克风「{0}」：{1}", mic.FriendlyName, e.Exception.Message));
                Stop();
            };
            _cap.StartRecording();
        }

        public void SetFx(VoiceFx.Settings s) => _source.Fx.Set(s);

        public void Stop()
        {
            if (!_alive) return;
            _alive = false;
            _ring.Close();
            Monitor?.Close();
            _rvcStream?.Dispose();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _cap.StopRecording(); } catch { }
                try { _cap.Dispose(); } catch { }
            });
        }
    }

    /// <summary>从录音缓冲读出来后过一遍变声效果，再抄一份给监听。</summary>
    private sealed class Processed(ISampleProvider src, VoiceFx fx, Ring? monitor) : ISampleProvider
    {
        public VoiceFx Fx => fx;
        public WaveFormat WaveFormat => src.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = src.Read(buffer, offset, count);
            if (n == 0) return 0;
            fx.Process(buffer, offset, n);
            monitor?.WriteMono(buffer, offset, n);
            return n;
        }
    }
}
