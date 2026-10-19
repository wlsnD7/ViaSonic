namespace ViaSonic.Audio;

/// <summary>
/// 变速不变调（WSOLA）：每次从源里取一帧（40ms，汉宁窗，输出步长 20ms），帧起点按速度前进，
/// 并在 ±10ms 内找和上一帧自然延续最像的位置，再重叠相加。
/// </summary>
internal sealed class Stretch
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
