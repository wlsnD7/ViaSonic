using System.Text.Json.Nodes;

namespace ViaSonic.Audio;

/// <summary>变声效果的一个参数：滑块范围、默认值、显示单位。</summary>
public sealed record FxParam(string Key, string Name, double Min, double Max, double Step, double Default, string Unit);

/// <summary>一种变声效果。</summary>
public sealed record FxDef(string Key, string Name, FxParam[] Params);

/// <summary>
/// 变声效果链（单声道）。顺序固定：噪声门 → 音高/音色 → 均衡 → 压缩 → 机器人 → 失真 → 低保真 → 无线电 → 合唱 → 镶边 → 移相 → 颤音 → 回声 → 混响 → 底噪 → 限幅。
/// 设置可以在任意线程更新，处理线程在下一块开始时换上。
/// </summary>
public sealed class VoiceFx
{
    public const int MaxEffects = 6;

    public static readonly FxDef[] Defs =
    [
        new("pitch", "音高", [new("semitones", "半音", -12, 12, 1, 0, "st")]), // 翻译键
        new("formant", "音色", [new("shift", "共振峰", -12, 12, 1, 0, "st")]), // 翻译键
        new("eq", "均衡", [new("bass", "低音", -12, 12, 1, 0, "dB"), new("treble", "高音", -12, 12, 1, 0, "dB")]), // 翻译键
        new("gate", "噪声门", [new("threshold", "阈值", -80, -20, 1, -50, "dB"), new("release", "释放", 20, 500, 10, 150, "ms")]), // 翻译键
        new("compressor", "压缩", [new("threshold", "阈值", -40, 0, 1, -20, "dB"), new("ratio", "比率", 1, 20, 0.5, 4, "ratio")]), // 翻译键
        new("distortion", "失真", [new("drive", "强度", 0, 100, 1, 40, "%")]), // 翻译键
        new("noise", "底噪", [new("level", "音量", 0, 100, 1, 20, "%")]), // 翻译键
        new("radio", "无线电", [new("low", "低切", 100, 1000, 10, 400, "Hz"), new("high", "高切", 1000, 8000, 100, 3000, "Hz")]), // 翻译键
        new("lofi", "低保真", [new("rate", "采样率", 2000, 16000, 100, 6000, "Hz"), new("bits", "位深", 2, 16, 1, 8, "")]), // 翻译键
        new("robot", "机器人", [new("freq", "频率", 20, 300, 1, 80, "Hz"), new("amount", "强度", 0, 100, 1, 70, "%")]), // 翻译键
        new("chorus", "合唱", [new("amount", "强度", 0, 100, 1, 50, "%")]), // 翻译键
        new("flanger", "镶边", [new("rate", "速度", 0.1, 5, 0.1, 0.5, "Hz"), new("depth", "深度", 0, 100, 1, 60, "%")]), // 翻译键
        new("phaser", "移相", [new("rate", "速度", 0.1, 5, 0.1, 0.5, "Hz"), new("depth", "深度", 0, 100, 1, 70, "%")]), // 翻译键
        new("vibrato", "颤音", [new("rate", "速度", 1, 12, 0.5, 6, "Hz"), new("depth", "深度", 0, 100, 1, 40, "%")]), // 翻译键
        new("echo", "回声", [new("delay", "延迟", 50, 1000, 10, 250, "ms"), new("feedback", "反馈", 0, 90, 1, 35, "%")]), // 翻译键
        new("reverb", "混响", [new("size", "大小", 0, 100, 1, 60, "%"), new("mix", "混合", 0, 100, 1, 30, "%")]), // 翻译键
    ];

    /// <summary>一组效果设置：开着的效果 → 参数值（按 FxDef.Params 的顺序）。不可变，换设置时整体替换。</summary>
    public sealed class Settings
    {
        public readonly Dictionary<string, double[]> On = [];
        public static readonly Settings None = new();

        public double Get(string fx, int i) => On.TryGetValue(fx, out var v) ? v[i] : 0;

        public static Settings Parse(JsonObject? effects)
        {
            var s = new Settings();
            if (effects == null) return s;
            foreach (var def in Defs)
            {
                if (effects[def.Key] is not JsonObject e || !(e["on"]?.GetValue<bool>() ?? false)) continue;
                s.On[def.Key] = def.Params.Select(p => Math.Clamp(e[p.Key] is { } n ? App.ToDouble(n) : p.Default, p.Min, p.Max)).ToArray();
            }
            return s;
        }
    }

    /// <summary>效果的默认设置（全部关闭，参数为默认值），新组合用这个。</summary>
    public static JsonObject DefaultEffects()
    {
        var o = new JsonObject();
        foreach (var def in Defs)
        {
            var e = new JsonObject { ["on"] = false };
            foreach (var p in def.Params) e[p.Key] = p.Default;
            o[def.Key] = e;
        }
        // AI 变声（RVC）也存在组合里：模型路径和 5 个旋钮（检索比例、响度因子按百分比存）
        o["rvc"] = new JsonObject { ["on"] = false, ["model"] = "", ["pitch"] = 0, ["formant"] = 0.0, ["index"] = 50, ["rms"] = 0, ["gate"] = -60 };
        return o;
    }

    private readonly int _rate;
    private volatile Settings _want = Settings.None;
    private Settings? _applied;

    private Gate? _gate;
    private Compressor? _comp;
    private Phaser? _phaser;
    private PitchShifter? _pitch;
    private Biquad? _bass, _treble;
    private Robot? _robot;
    private float _drive;
    private LoFi? _lofi;
    private Biquad[]? _radio;
    private Modulated? _chorus, _flanger, _vibrato;
    private Echo? _echo;
    private Reverb? _reverb;
    private Noise? _noise;
    private readonly Limiter _limiter;

    public VoiceFx(int rate)
    {
        _rate = rate;
        _limiter = new Limiter(rate);
    }

    public void Set(Settings s) => _want = s;

    /// <summary>音高/音色带来的延迟（采样）。</summary>
    public int Latency => _pitch?.Latency ?? 0;

    private void Apply(Settings s)
    {
        _applied = s;
        bool pitch = s.On.ContainsKey("pitch") || s.On.ContainsKey("formant");
        if (pitch)
        {
            _pitch ??= new PitchShifter(_rate);
            _pitch.Pitch = Math.Pow(2, s.Get("pitch", 0) / 12);
            _pitch.Formant = Math.Pow(2, s.Get("formant", 0) / 12);
        }
        else _pitch = null;

        if (s.On.ContainsKey("eq"))
        {
            (_bass ??= new Biquad()).LowShelf(_rate, 200, s.Get("eq", 0));
            (_treble ??= new Biquad()).HighShelf(_rate, 3500, s.Get("eq", 1));
        }
        else _bass = _treble = null;

        if (s.On.ContainsKey("gate")) (_gate ??= new Gate(_rate)).Set(s.Get("gate", 0), s.Get("gate", 1));
        else _gate = null;
        if (s.On.ContainsKey("compressor")) (_comp ??= new Compressor(_rate)).Set(s.Get("compressor", 0), s.Get("compressor", 1));
        else _comp = null;
        if (s.On.ContainsKey("phaser")) (_phaser ??= new Phaser(_rate)).Set(s.Get("phaser", 0), s.Get("phaser", 1) / 100);
        else _phaser = null;

        if (s.On.ContainsKey("robot")) (_robot ??= new Robot(_rate)).Set(s.Get("robot", 0), s.Get("robot", 1) / 100);
        else _robot = null;

        _drive = s.On.ContainsKey("distortion") ? (float)(s.Get("distortion", 0) / 100) : 0;

        if (s.On.ContainsKey("lofi")) (_lofi ??= new LoFi(_rate)).Set(s.Get("lofi", 0), (int)s.Get("lofi", 1));
        else _lofi = null;

        if (s.On.ContainsKey("radio"))
        {
            _radio ??= [new Biquad(), new Biquad(), new Biquad(), new Biquad()];
            _radio[0].HighPass(_rate, s.Get("radio", 0)); _radio[1].HighPass(_rate, s.Get("radio", 0));
            _radio[2].LowPass(_rate, s.Get("radio", 1)); _radio[3].LowPass(_rate, s.Get("radio", 1));
        }
        else _radio = null;

        if (s.On.ContainsKey("chorus")) (_chorus ??= Modulated.Chorus(_rate)).Mix = (float)(s.Get("chorus", 0) / 100);
        else _chorus = null;
        if (s.On.ContainsKey("flanger")) (_flanger ??= Modulated.Flanger(_rate)).SetRateDepth(s.Get("flanger", 0), s.Get("flanger", 1) / 100);
        else _flanger = null;
        if (s.On.ContainsKey("vibrato")) (_vibrato ??= Modulated.Vibrato(_rate)).SetRateDepth(s.Get("vibrato", 0), s.Get("vibrato", 1) / 100);
        else _vibrato = null;

        if (s.On.ContainsKey("echo")) (_echo ??= new Echo(_rate)).Set(s.Get("echo", 0), s.Get("echo", 1) / 100);
        else _echo = null;
        if (s.On.ContainsKey("reverb")) (_reverb ??= new Reverb(_rate)).Set(s.Get("reverb", 0) / 100, s.Get("reverb", 1) / 100);
        else _reverb = null;
        if (s.On.ContainsKey("noise")) (_noise ??= new Noise(_rate)).Level = (float)(s.Get("noise", 0) / 100 * 0.05);
        else _noise = null;
    }

    public void Process(float[] buf, int offset, int count)
    {
        var want = _want;
        if (!ReferenceEquals(want, _applied)) Apply(want);
        for (int i = offset; i < offset + count; i++)
        {
            float x = buf[i];
            if (_gate != null) x = _gate.Process(x);
            if (_pitch != null) x = _pitch.Process(x);
            if (_bass != null) x = _treble!.Process(_bass.Process(x));
            if (_comp != null) x = _comp.Process(x);
            if (_robot != null) x = _robot.Process(x);
            if (_drive > 0)
            {
                float g = 1 + _drive * 30;
                x = MathF.Tanh(g * x) / MathF.Tanh(g * 0.3f) * 0.3f;
            }
            if (_lofi != null) x = _lofi.Process(x);
            if (_radio != null) foreach (var f in _radio) x = f.Process(x);
            if (_chorus != null) x = _chorus.Process(x);
            if (_flanger != null) x = _flanger.Process(x);
            if (_phaser != null) x = _phaser.Process(x);
            if (_vibrato != null) x = _vibrato.Process(x);
            if (_echo != null) x = _echo.Process(x);
            if (_reverb != null) x = _reverb.Process(x);
            if (_noise != null) x += _noise.Next();
            buf[i] = _limiter.Process(x);
        }
    }

    /// <summary>整段处理（朗读的语音）：去掉音高处理的延迟，有回声/混响时留出尾音。</summary>
    public static Clip ProcessClip(Clip clip, Settings s)
    {
        if (s.On.Count == 0) return clip;
        var fx = new VoiceFx(clip.SampleRate);
        fx.Set(s);
        fx.Apply(s);
        int lat = fx.Latency;
        int tail = s.On.ContainsKey("echo") || s.On.ContainsKey("reverb") ? clip.SampleRate * 3 / 2 : 0;
        var buf = new float[clip.Samples.Length + lat + tail];
        Array.Copy(clip.Samples, buf, clip.Samples.Length);
        fx.Process(buf, 0, buf.Length);
        var len = buf.Length - lat;
        if (tail > 0 && !s.On.ContainsKey("noise"))
            while (len > clip.Samples.Length && Math.Abs(buf[lat + len - 1]) < 1e-4f) len--;
        var outp = new float[len];
        Array.Copy(buf, lat, outp, 0, len);
        return new Clip(outp, clip.SampleRate);
    }

    // ------------------------------------------------------------------ 各个效果

    /// <summary>
    /// 相位声码器变调（参照 S. M. Bernsee 的 smbPitchShift），并用倒谱平滑得到频谱包络：
    /// 音高只移动谐波、保留共振峰（声音更自然）；音色单独移动共振峰（男声 ↔ 女声的感觉）。
    /// </summary>
    private sealed class PitchShifter
    {
        private const int Osamp = 4;
        private readonly int _n, _hop, _half, _lifter;
        private readonly float[] _win, _inFifo, _outFifo, _accum, _lastPhase, _sumPhase;
        private readonly float[] _re, _im, _mag, _freq, _env, _synMag, _synFreq;
        private int _rover;

        public double Pitch = 1, Formant = 1;
        public int Latency => _n - _hop;

        public PitchShifter(int rate)
        {
            _n = rate > 48000 ? 2048 : 1024;
            _hop = _n / Osamp;
            _half = _n / 2;
            _lifter = Math.Max(8, rate * 12 / 10000); // 约 1.2ms 以下的倒谱 = 包络
            _win = new float[_n];
            for (int k = 0; k < _n; k++) _win[k] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * k / _n));
            _inFifo = new float[_n]; _outFifo = new float[_n]; _accum = new float[2 * _n];
            _lastPhase = new float[_half + 1]; _sumPhase = new float[_half + 1];
            _re = new float[_n]; _im = new float[_n];
            _mag = new float[_half + 1]; _freq = new float[_half + 1]; _env = new float[_half + 1];
            _synMag = new float[_half + 1]; _synFreq = new float[_half + 1];
            _rover = Latency;
        }

        public float Process(float x)
        {
            _inFifo[_rover] = x;
            float y = _outFifo[_rover - Latency];
            if (++_rover >= _n)
            {
                _rover = Latency;
                Frame();
            }
            return y;
        }

        private void Frame()
        {
            double expct = 2 * Math.PI * _hop / _n;
            for (int k = 0; k < _n; k++) { _re[k] = _inFifo[k] * _win[k]; _im[k] = 0; }
            Fft.Run(_re, _im, false);
            for (int k = 0; k <= _half; k++)
            {
                double re = _re[k], im = _im[k];
                _mag[k] = (float)(2 * Math.Sqrt(re * re + im * im));
                double phase = Math.Atan2(im, re);
                double tmp = phase - _lastPhase[k];
                _lastPhase[k] = (float)phase;
                tmp -= k * expct;
                long qpd = (long)(tmp / Math.PI);
                qpd += qpd >= 0 ? qpd & 1 : -(qpd & 1);
                tmp -= Math.PI * qpd;
                _freq[k] = (float)(k + Osamp * tmp / (2 * Math.PI));
            }

            Envelope();
            Array.Clear(_synMag);
            Array.Clear(_synFreq);
            for (int k = 0; k <= _half; k++)
            {
                int idx = (int)Math.Round(k * Pitch);
                if (idx > _half) break;
                _synMag[idx] += _mag[k] / _env[k];
                _synFreq[idx] = (float)(_freq[k] * Pitch);
            }
            for (int k = 0; k <= _half; k++)
            {
                double src = k / Formant; // 共振峰移到 k 处
                int i = (int)src;
                float e = i >= _half ? _env[_half] : _env[i] + (_env[i + 1] - _env[i]) * (float)(src - i);
                _synMag[k] *= e;
            }

            for (int k = 0; k <= _half; k++)
            {
                double tmp = _synFreq[k] - k;
                tmp = 2 * Math.PI * tmp / Osamp + k * expct;
                _sumPhase[k] += (float)tmp;
                _re[k] = (float)(_synMag[k] * Math.Cos(_sumPhase[k]));
                _im[k] = (float)(_synMag[k] * Math.Sin(_sumPhase[k]));
            }
            for (int k = _half + 1; k < _n; k++) { _re[k] = 0; _im[k] = 0; }
            Fft.Run(_re, _im, true);
            for (int k = 0; k < _n; k++) _accum[k] += 2f * _win[k] * _re[k] / (_half * Osamp);
            Array.Copy(_accum, _outFifo, _hop);
            Array.Copy(_accum, _hop, _accum, 0, _n);
            Array.Clear(_accum, _n, _n);
            Array.Copy(_inFifo, _hop, _inFifo, 0, Latency);
        }

        /// <summary>倒谱平滑：log 幅度 → 倒谱 → 只留低阶 → 回到频谱。</summary>
        private void Envelope()
        {
            for (int k = 0; k <= _half; k++) { _re[k] = MathF.Log(_mag[k] + 1e-7f); _im[k] = 0; }
            for (int k = 1; k < _half; k++) { _re[_n - k] = _re[k]; _im[_n - k] = 0; }
            Fft.Run(_re, _im, true);
            for (int k = 0; k < _n; k++)
            {
                bool keep = k <= _lifter || k >= _n - _lifter;
                _re[k] = keep ? _re[k] / _n : 0;
                _im[k] = keep ? _im[k] / _n : 0;
            }
            Fft.Run(_re, _im, false);
            for (int k = 0; k <= _half; k++) _env[k] = MathF.Max(MathF.Exp(_re[k]), 1e-7f);
        }
    }

    /// <summary>噪声门：电平低于阈值时关上（保持 50ms 再按释放时间淡出），高于阈值时 1ms 打开。</summary>
    private sealed class Gate(int rate)
    {
        private readonly float _envRelease = (float)Math.Exp(-1.0 / (rate * 0.02));
        private readonly float _open = (float)Math.Exp(-1.0 / (rate * 0.001));
        private readonly int _holdLen = rate / 20;
        private float _thr, _close, _env, _gain;
        private int _hold;

        public void Set(double thresholdDb, double releaseMs)
        {
            _thr = (float)Math.Pow(10, thresholdDb / 20);
            _close = (float)Math.Exp(-1.0 / (rate * releaseMs / 1000));
        }

        public float Process(float x)
        {
            float a = MathF.Abs(x);
            _env = a > _env ? a : _env * _envRelease + a * (1 - _envRelease);
            if (_env >= _thr) _hold = _holdLen;
            else if (_hold > 0) _hold--;
            float target = _hold > 0 ? 1 : 0;
            float c = target > _gain ? _open : _close;
            _gain = target + (_gain - target) * c;
            return x * _gain;
        }
    }

    /// <summary>压缩：电平（dB）超过阈值的部分按比率压低，自动补偿一半的最大压缩量。启动 5ms，释放 120ms。</summary>
    private sealed class Compressor(int rate)
    {
        private readonly float _attack = (float)Math.Exp(-1.0 / (rate * 0.005));
        private readonly float _release = (float)Math.Exp(-1.0 / (rate * 0.12));
        private float _thrDb, _slope, _makeupDb, _envDb = -120;

        public void Set(double thresholdDb, double ratio)
        {
            _thrDb = (float)thresholdDb;
            _slope = 1 - 1 / (float)Math.Max(1, ratio);
            _makeupDb = -_thrDb * _slope / 2;
        }

        public float Process(float x)
        {
            float db = 20 * MathF.Log10(MathF.Abs(x) + 1e-6f);
            float c = db > _envDb ? _attack : _release;
            _envDb = db + (_envDb - db) * c;
            float reduce = _envDb > _thrDb ? (_envDb - _thrDb) * _slope : 0;
            return x * MathF.Pow(10, (_makeupDb - reduce) / 20);
        }
    }

    /// <summary>移相：4 级一阶全通，转折频率随低频振荡在 200Hz 到 200Hz+深度×1.8kHz 之间扫动，和原声混合。</summary>
    private sealed class Phaser(int rate)
    {
        private readonly float[] _x1 = new float[4], _y1 = new float[4];
        private double _phase, _step, _depth;
        private float _last;

        public void Set(double rateHz, double depth)
        {
            _step = 2 * Math.PI * rateHz / rate;
            _depth = depth;
        }

        public float Process(float x)
        {
            _phase = (_phase + _step) % (2 * Math.PI);
            double f = 200 * Math.Pow(1 + _depth * 9, (1 + Math.Sin(_phase)) / 2);
            double t = Math.Tan(Math.PI * Math.Min(f, rate * 0.45) / rate);
            float a = (float)((t - 1) / (t + 1));
            float y = x + _last * 0.5f;
            for (int k = 0; k < 4; k++)
            {
                float o = a * y + _x1[k] - a * _y1[k];
                _x1[k] = y;
                _y1[k] = o;
                y = o;
            }
            _last = y;
            return (x + y) * 0.6f;
        }
    }

    /// <summary>环形调制。</summary>
    private sealed class Robot(int rate)
    {
        private double _phase, _step;
        private float _amount;

        public void Set(double freq, double amount)
        {
            _step = 2 * Math.PI * freq / rate;
            _amount = (float)amount;
        }

        public float Process(float x)
        {
            _phase = (_phase + _step) % (2 * Math.PI);
            return x * (1 - _amount) + x * (float)Math.Sin(_phase) * _amount * 1.4f;
        }
    }

    /// <summary>降采样（采样保持）+ 降位深。</summary>
    private sealed class LoFi(int rate)
    {
        private double _step, _acc;
        private float _held, _levels;

        public void Set(double target, int bits)
        {
            _step = Math.Min(1, target / rate);
            _levels = MathF.Pow(2, bits - 1);
        }

        public float Process(float x)
        {
            _acc += _step;
            if (_acc >= 1)
            {
                _acc -= 1;
                _held = MathF.Round(x * _levels) / _levels;
            }
            return _held;
        }
    }

    /// <summary>调制延迟：合唱、镶边、颤音。</summary>
    private sealed class Modulated
    {
        private readonly int _rate;
        private readonly DelayLine _line;
        private readonly double[] _phases, _rates;
        private double _baseMs, _depthMs, _feedback, _rateHz = 1, _depth = 1;
        private float _last;
        private bool _wetOnly;

        public float Mix = 0.5f;

        private Modulated(int rate, int voices)
        {
            _rate = rate;
            _line = new DelayLine(rate / 10);
            _phases = new double[voices];
            _rates = new double[voices];
            for (int v = 0; v < voices; v++) _phases[v] = v * 2 * Math.PI / voices;
        }

        public static Modulated Chorus(int rate)
        {
            var m = new Modulated(rate, 3) { _baseMs = 20, _depthMs = 4 };
            m._rates[0] = 0.31; m._rates[1] = 0.47; m._rates[2] = 0.73;
            return m;
        }

        public static Modulated Flanger(int rate) => new(rate, 1) { _baseMs = 1, _depthMs = 4, _feedback = 0.6, Mix = 0.5f };

        public static Modulated Vibrato(int rate) => new(rate, 1) { _baseMs = 6, _depthMs = 5, _wetOnly = true, Mix = 1 };

        public void SetRateDepth(double rateHz, double depth)
        {
            _rateHz = rateHz;
            _depth = depth;
            _rates[0] = rateHz;
        }

        public float Process(float x)
        {
            _line.Write(x + _last * (float)_feedback);
            float wet = 0;
            for (int v = 0; v < _phases.Length; v++)
            {
                _phases[v] = (_phases[v] + 2 * Math.PI * _rates[v] / _rate) % (2 * Math.PI);
                double ms = _baseMs + _depthMs * _depth * (1 + Math.Sin(_phases[v])) / 2;
                wet += _line.Read(ms * _rate / 1000);
            }
            wet /= _phases.Length;
            _last = wet;
            return _wetOnly ? wet : x * (1 - Mix * 0.5f) + wet * Mix;
        }
    }

    private sealed class Echo(int rate)
    {
        private readonly DelayLine _line = new(rate * 11 / 10);
        private double _delay;
        private float _feedback;

        public void Set(double ms, double feedback)
        {
            _delay = ms * rate / 1000;
            _feedback = (float)feedback;
        }

        public float Process(float x)
        {
            float d = _line.Read(_delay);
            _line.Write(x + d * _feedback);
            return x + d * 0.6f;
        }
    }

    /// <summary>简化的 Freeverb：8 个梳状滤波 + 4 个全通。</summary>
    private sealed class Reverb
    {
        private static readonly int[] CombLen = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
        private static readonly int[] AllLen = [556, 441, 341, 225];
        private readonly float[][] _comb, _all;
        private readonly int[] _ci, _ai;
        private readonly float[] _store;
        private float _feedback = 0.84f, _mix = 0.3f;

        public Reverb(int rate)
        {
            double s = rate / 44100.0;
            _comb = CombLen.Select(l => new float[(int)(l * s)]).ToArray();
            _all = AllLen.Select(l => new float[(int)(l * s)]).ToArray();
            _ci = new int[_comb.Length];
            _ai = new int[_all.Length];
            _store = new float[_comb.Length];
        }

        public void Set(double size, double mix)
        {
            _feedback = (float)(0.7 + size * 0.28);
            _mix = (float)mix;
        }

        public float Process(float x)
        {
            float input = x * 0.015f, outp = 0;
            for (int c = 0; c < _comb.Length; c++)
            {
                var b = _comb[c];
                float y = b[_ci[c]];
                _store[c] = y * 0.8f + _store[c] * 0.2f; // 阻尼
                b[_ci[c]] = input + _store[c] * _feedback;
                _ci[c] = (_ci[c] + 1) % b.Length;
                outp += y;
            }
            for (int a = 0; a < _all.Length; a++)
            {
                var b = _all[a];
                float buf = b[_ai[a]];
                b[_ai[a]] = outp + buf * 0.5f;
                outp = buf - outp;
                _ai[a] = (_ai[a] + 1) % b.Length;
            }
            return x * (1 - _mix * 0.5f) + outp * _mix * 3;
        }
    }

    /// <summary>底噪：低通过的白噪声。</summary>
    private sealed class Noise(int rate)
    {
        private readonly Random _rng = new();
        private readonly float _a = (float)Math.Exp(-2 * Math.PI * 6000 / rate);
        private float _lp;
        public float Level;

        public float Next()
        {
            float w = (float)(_rng.NextDouble() * 2 - 1);
            _lp = w * (1 - _a) + _lp * _a;
            return _lp * Level * 2;
        }
    }

    /// <summary>峰值限幅：超过 -1 dB 时立刻压下来，约 100ms 恢复。</summary>
    private sealed class Limiter(int rate)
    {
        private readonly float _release = (float)Math.Exp(-1.0 / (rate * 0.1));
        private float _env;

        public float Process(float x)
        {
            float a = MathF.Abs(x);
            _env = a > _env ? a : _env * _release + a * (1 - _release);
            return _env > 0.89f ? x * 0.89f / _env : x;
        }
    }
}
