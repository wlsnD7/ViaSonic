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
            _ring = new Ring(wf.SampleRate, owner);
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

    /// <summary>录音 → 单声道环形缓冲。积到 20ms 才开始输出，欠载时补静音并重新积攒；积压超过 100ms 时丢掉旧的，防止时钟漂移导致延迟越来越大。</summary>
    private sealed class Ring(int rate, Passthrough owner) : ISampleProvider
    {
        private readonly float[] _buf = new float[rate];
        private readonly int _prime = rate * 20 / 1000, _max = rate * 100 / 1000, _trim = rate * 30 / 1000;
        private int _head, _count;
        private bool _priming = true;
        private volatile bool _closed;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);

        public void Close() => _closed = true;

        public void Write(byte[] data, int bytes, WaveFormat wf)
        {
            int ch = wf.Channels, bps = wf.BitsPerSample / 8, frames = bytes / wf.BlockAlign;
            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
                           || (wf is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            lock (_buf)
            {
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int o = f * wf.BlockAlign + c * bps;
                        sum += isFloat ? BitConverter.ToSingle(data, o)
                            : bps == 2 ? BitConverter.ToInt16(data, o) / 32768f
                            : bps == 3 ? ((data[o] << 8 | data[o + 1] << 16 | data[o + 2] << 24) >> 8) / 8388608f
                            : BitConverter.ToInt32(data, o) / 2147483648f;
                    }
                    _buf[(_head + _count) % _buf.Length] = sum / ch;
                    if (_count < _buf.Length) _count++;
                    else _head = (_head + 1) % _buf.Length;
                }
                if (_count > _max)
                {
                    _head = (_head + _count - _trim) % _buf.Length;
                    _count = _trim;
                }
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_closed) return 0;
            float vol = owner.Volume;
            int n = 0;
            lock (_buf)
            {
                if (_priming && _count >= _prime) _priming = false;
                if (!_priming)
                {
                    n = Math.Min(count, _count);
                    for (int i = 0; i < n; i++) buffer[offset + i] = _buf[(_head + i) % _buf.Length] * vol;
                    _head = (_head + n) % _buf.Length;
                    _count -= n;
                    if (n < count) _priming = true;
                }
            }
            for (int i = n; i < count; i++) buffer[offset + i] = 0;
            return count;
        }
    }
}
