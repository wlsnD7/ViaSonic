using NAudio.Wave;

namespace ViaSonic.Audio;

/// <summary>一次播放：在每个输出设备上各有一路流，全部读完（或被停止）时 Done。start：前置静音之后从第几个采样开始。</summary>
/// <param name="gain">音量；参数为 true 时是送到监听设备的那一路。</param>
/// <param name="speed">播放速度，null＝原速（不循环时才生效）。</param>
internal sealed class Voice(Clip clip, int leadMs, Func<bool> loop, Func<bool, float> gain, int start, Func<double>? speed = null)
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
