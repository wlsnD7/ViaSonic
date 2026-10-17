using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ViaSonic.Audio;

/// <summary>一个输出设备上常开的混音流：语音、音效、麦克风直通都作为输入加进来，空闲一会儿后自动关闭。</summary>
public sealed class Bus : IDisposable
{
    private sealed record Input(ISampleProvider Source, Action Ended);

    private readonly List<Input> _inputs = [];
    private readonly WasapiOut _out;
    private readonly Timer _idle;
    private float[] _scratch = [];
    private DateTime _lastUsed = DateTime.UtcNow;
    private volatile bool _dead;
    private int _closed;

    public string DeviceId { get; }
    public string Name { get; }
    public WaveFormat Format { get; }
    public bool Dead => _dead;
    /// <summary>空闲关闭后从设备池移除。</summary>
    public event Action<Bus>? Closed;

    public Bus(MMDevice dev)
    {
        DeviceId = dev.ID;
        Name = dev.FriendlyName;
        var mix = dev.AudioClient.MixFormat;
        Format = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        _out = new WasapiOut(dev, AudioClientShareMode.Shared, true, 40);
        _out.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) Log.Error(T("播放到「{0}」失败：{1}", Name, e.Exception.Message));
            Close();
        };
        _out.Init(new Reader(this));
        _out.Play();
        _idle = new Timer(_ => CheckIdle(), null, 1000, 1000);
    }

    /// <summary>加入一路单声道输入（任意采样率），读完（返回不足）时移除并回调 ended。已关闭时返回 false。</summary>
    public bool Add(ISampleProvider mono, Action ended)
    {
        ISampleProvider sp = mono;
        if (sp.WaveFormat.SampleRate != Format.SampleRate) sp = new WdlResamplingSampleProvider(sp, Format.SampleRate);
        sp = new SpreadChannels(sp, Format.Channels);
        lock (_inputs)
        {
            if (_dead) return false;
            _inputs.Add(new Input(sp, ended));
        }
        return true;
    }

    private void CheckIdle()
    {
        lock (_inputs)
        {
            if (_inputs.Count > 0) _lastUsed = DateTime.UtcNow;
            else if (DateTime.UtcNow - _lastUsed > TimeSpan.FromSeconds(3)) _dead = true;
        }
        if (_dead) Close();
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        List<Input> left;
        lock (_inputs)
        {
            _dead = true;
            left = [.. _inputs];
            _inputs.Clear();
        }
        _idle.Dispose();
        foreach (var i in left) i.Ended();
        Closed?.Invoke(this);
        // 在 WASAPI 自己的线程里不能 Dispose，换个线程
        ThreadPool.QueueUserWorkItem(_ => { try { _out.Dispose(); } catch { } });
    }

    public void Dispose() => Close();

    private int Mix(float[] buffer, int offset, int count)
    {
        // buffer 是 NAudio 用 WaveBuffer 把 byte[] 当成 float[] 传进来的，Array.Clear 会按字节算长度，只能逐个清零
        for (int k = 0; k < count; k++) buffer[offset + k] = 0;
        if (_scratch.Length < count) _scratch = new float[count];
        List<Input>? ended = null;
        lock (_inputs)
        {
            foreach (var i in _inputs)
            {
                int n;
                try { n = i.Source.Read(_scratch, 0, count); }
                catch (Exception e) { Log.Warn(e.Message); n = 0; }
                for (int k = 0; k < n; k++) buffer[offset + k] += _scratch[k];
                if (n < count) (ended ??= []).Add(i);
            }
            if (ended != null) foreach (var i in ended) _inputs.Remove(i);
        }
        if (ended != null) foreach (var i in ended) i.Ended();
        for (int k = 0; k < count; k++) buffer[offset + k] = Math.Clamp(buffer[offset + k], -1f, 1f);
        return count;
    }

    private sealed class Reader(Bus bus) : ISampleProvider
    {
        public WaveFormat WaveFormat => bus.Format;
        public int Read(float[] buffer, int offset, int count) => bus.Mix(buffer, offset, count);
    }

    /// <summary>单声道铺到设备的所有声道。</summary>
    private sealed class SpreadChannels(ISampleProvider src, int channels) : ISampleProvider
    {
        private float[] _mono = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / channels;
            if (_mono.Length < frames) _mono = new float[frames];
            int n = src.Read(_mono, 0, frames);
            for (int i = 0; i < n; i++)
                for (int c = 0; c < channels; c++) buffer[offset + i * channels + c] = _mono[i];
            return n * channels;
        }
    }
}

/// <summary>按设备复用 Bus。</summary>
public sealed class BusPool
{
    private readonly Dictionary<string, Bus> _buses = [];

    /// <summary>把输入加到设备的混音流上；刚好空闲关闭时换一个新的。</summary>
    public bool Add(MMDevice dev, ISampleProvider mono, Action ended)
    {
        for (int i = 0; i < 2; i++)
            if (Get(dev) is { } b && b.Add(mono, ended)) return true;
        return false;
    }

    private Bus? Get(MMDevice dev)
    {
        lock (_buses)
        {
            if (_buses.TryGetValue(dev.ID, out var b) && !b.Dead) return b;
            try
            {
                b = new Bus(dev);
            }
            catch (Exception e)
            {
                Log.Error(T("播放到「{0}」失败：{1}", dev.FriendlyName, e.Message));
                return null;
            }
            b.Closed += x => { lock (_buses) if (_buses.TryGetValue(x.DeviceId, out var cur) && cur == x) _buses.Remove(x.DeviceId); };
            _buses[dev.ID] = b;
            return b;
        }
    }

    public void CloseAll()
    {
        List<Bus> all;
        lock (_buses) all = [.. _buses.Values];
        foreach (var b in all) b.Dispose();
    }
}
