namespace ViaSonic.Audio;

/// <summary>变声用的基础 DSP 组件：FFT、双二阶滤波、分数延迟线。</summary>
internal static class Fft
{
    /// <summary>原地复数 FFT（基 2）。inverse 时不做 1/N 缩放。</summary>
    public static void Run(float[] re, float[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}

/// <summary>RBJ 双二阶滤波器。</summary>
internal sealed class Biquad
{
    private float _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    private void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0); _b1 = (float)(b1 / a0); _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0); _a2 = (float)(a2 / a0);
    }

    public void LowPass(double rate, double freq, double q = 0.7071)
    {
        double w = 2 * Math.PI * Math.Min(freq, rate * 0.45) / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        Set((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void HighPass(double rate, double freq, double q = 0.7071)
    {
        double w = 2 * Math.PI * Math.Min(freq, rate * 0.45) / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        Set((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public void LowShelf(double rate, double freq, double db) => Shelf(rate, freq, db, low: true);
    public void HighShelf(double rate, double freq, double db) => Shelf(rate, freq, db, low: false);

    private void Shelf(double rate, double freq, double db, bool low)
    {
        double a = Math.Pow(10, db / 40), w = 2 * Math.PI * Math.Min(freq, rate * 0.45) / rate;
        double cos = Math.Cos(w), alpha = Math.Sin(w) / 2 * Math.Sqrt(2), s = 2 * Math.Sqrt(a) * alpha;
        if (low)
            Set(a * ((a + 1) - (a - 1) * cos + s), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - s),
                (a + 1) + (a - 1) * cos + s, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - s);
        else
            Set(a * ((a + 1) + (a - 1) * cos + s), -2 * a * ((a - 1) + (a + 1) * cos), a * ((a + 1) + (a - 1) * cos - s),
                (a + 1) - (a - 1) * cos + s, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - s);
    }
}

/// <summary>环形延迟线，支持分数延迟（线性插值）。</summary>
internal sealed class DelayLine(int size)
{
    private readonly float[] _buf = new float[size];
    private int _pos;

    public void Write(float x)
    {
        _buf[_pos] = x;
        _pos = (_pos + 1) % _buf.Length;
    }

    /// <summary>delay 个采样之前的值（1 ≤ delay &lt; size）。</summary>
    public float Read(double delay)
    {
        delay = Math.Clamp(delay, 1, _buf.Length - 2);
        double p = _pos - delay;
        if (p < 0) p += _buf.Length;
        int i = (int)p;
        float f = (float)(p - i);
        return _buf[i] * (1 - f) + _buf[(i + 1) % _buf.Length] * f;
    }
}
