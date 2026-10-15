using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ViaTTS.Audio;

/// <summary>麦克风直通：录真实麦克风，作为一路常驻输入加到虚拟麦克风的混音流里。设备断开后定时重试。</summary>
public sealed class Passthrough
{
    private readonly BusPool _pool;
    private readonly Timer _retry;
    private readonly object _lock = new();
    private (string Cable, string Mic)? _want;
    private Session? _cur;
    private string _lastError = "";

    public volatile float Volume = 1f;

    public Passthrough(BusPool pool)
    {
        _pool = pool;
        _retry = new Timer(_ => Reconcile(), null, 3000, 3000);
    }

    /// <summary>设置目标：虚拟麦克风设备名（空＝自动）和麦克风设备名（空＝默认）；null 表示关闭。</summary>
    public void Set((string Cable, string Mic)? want)
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
                if (Devices.IsCable(micName)) return; // 录虚拟麦克风自己会形成回环
                var s = new Session(mic, this);
                if (!_pool.Add(cable, s.Source, s.Stop))
                {
                    s.Stop();
                    return;
                }
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
        private volatile bool _alive = true;

        public ISampleProvider Source => _ring;
        public bool Alive => _alive;

        public Session(MMDevice mic, Passthrough owner)
        {
            _cap = new WasapiCapture(mic, true, 20);
            var wf = _cap.WaveFormat;
            _ring = new Ring(wf.SampleRate, () => owner.Volume);
            _cap.DataAvailable += (_, e) => _ring.Write(e.Buffer, e.BytesRecorded, wf);
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
            _ring.Close();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { _cap.StopRecording(); } catch { }
                try { _cap.Dispose(); } catch { }
            });
        }
    }
}
