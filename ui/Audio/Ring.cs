using NAudio.Wave;

namespace ViaSonic.Audio;

/// <summary>录音（麦克风直通、应用声音）→ 单声道环形缓冲。积到 20ms 才开始输出，欠载时补静音并重新积攒；积压超过 100ms 时丢掉旧的，防止时钟漂移导致延迟越来越大。</summary>
internal sealed class Ring(int rate, Func<float> volume) : ISampleProvider
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

    /// <summary>写入已经是单声道 float 的数据（变声后的麦克风送去监听）。</summary>
    public void WriteMono(float[] data, int offset, int count)
    {
        lock (_buf)
        {
            for (int i = 0; i < count; i++)
            {
                _buf[(_head + _count) % _buf.Length] = data[offset + i];
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
        float vol = volume();
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
