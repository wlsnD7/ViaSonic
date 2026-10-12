using System.Text.Json.Nodes;
using System.Threading.Channels;
using ViaTTS.Audio;
using ViaTTS.Engines;

namespace ViaTTS;

/// <summary>合成调度：单线程合成队列（避免 GPU 并发）、常用语预生成缓存、播放。</summary>
public sealed class Speaker : IDisposable
{
    private sealed record Job(int Gen, string Text, bool Cache, bool Play, string Tag = "");

    private readonly Func<JsonObject> _cfg;
    private readonly Channel<Job> _jobs = Channel.CreateUnbounded<Job>();
    private readonly Dictionary<string, Clip> _cache = [];
    private readonly LinkedList<string> _cacheOrder = new();
    private readonly Dictionary<string, (DateTime Mtime, Clip Clip)> _files = [];
    private int _gen;

    public Dictionary<string, ITtsEngine> Engines { get; }
    public GsvEngine Gsv { get; } = new();
    public Player Player { get; }
    public string Busy { get; private set; } = "";
    public string LastText { get; private set; } = "";

    /// <summary>状态有变化（可能在任意线程触发）。</summary>
    public event Action? Changed;
    public event Action<string>? Failed;

    public Speaker(Func<JsonObject> cfg)
    {
        _cfg = cfg;
        Engines = new() { ["gsv"] = Gsv, ["edge"] = new EdgeTts(), ["sapi"] = new SystemTts() };
        Gsv.Changed += () =>
        {
            Changed?.Invoke();
            if (Gsv.Ready && !Gsv.Loading && Gsv.Error.Length == 0 && CurrentKey == "gsv") Preload();
            // 缺 GPT-SoVITS / 缺模型文件由主页提示条说明，这里只报意外错误
            if (Gsv.Error.Length > 0 && Gsv.ErrorKind.Length == 0) Failed?.Invoke(Gsv.Error);
        };
        Player = new Player(cfg);
        Player.PlayingChanged += p => SetBusy(p ? "播放中" : ""); // 翻译键
        _ = Task.Run(WorkerAsync);
    }

    private JsonObject C => _cfg();
    private string CurrentKey => C["engine"]?.GetValue<string>() ?? "edge";
    public ITtsEngine Current => Engines.TryGetValue(CurrentKey, out var e) ? e : Engines["edge"];

    private void SetBusy(string s)
    {
        Busy = s;
        Changed?.Invoke();
    }

    public string StatusText
    {
        get
        {
            var e = Current;
            var s = e.Ready ? T("就绪") : e.Loading ? T("加载中") : T("未加载");
            return $"{e.Label} · {s}";
        }
    }

    // ------------------------------------------------------------------ 引擎
    public void LoadEngine() => Current.Load(C);

    public void OnEngineChanged()
    {
        Changed?.Invoke();
        if (!Current.Ready) Current.Load(C);
        else Preload();
    }

    /// <summary>GPT-SoVITS 的目录、设备、模型或模型文件变了。</summary>
    public void OnModelChanged()
    {
        if (CurrentKey == "gsv") Gsv.Load(C);
    }

    // ------------------------------------------------------------------ 朗读
    /// <summary>tag：常用语的 id，用来在常用语页高亮正在朗读的那一条。</summary>
    public void Speak(string? text, bool cache = false, string tag = "")
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) return;
        LastText = text;
        if (C["play_mode"]?.GetValue<string>() == "interrupt") Interlocked.Increment(ref _gen); // 丢掉还没合成的旧句子
        _jobs.Writer.TryWrite(new Job(_gen, text, cache, true, tag));
    }

    public void Repeat()
    {
        if (LastText.Length > 0) Speak(LastText, cache: true);
    }

    /// <summary>停止全部：语音和音效。</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _gen);
        Player.Stop();
    }

    /// <summary>只停止语音（包括还没合成完的）。</summary>
    public void StopSpeech()
    {
        Interlocked.Increment(ref _gen);
        Player.StopSpeech();
    }

    /// <summary>常用语的音频文件，和语音叠加播放。</summary>
    public void PlaySound(string id, string path) => _ = Task.Run(() =>
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            Clip clip;
            lock (_files)
            {
                if (_files.TryGetValue(path, out var hit) && hit.Mtime == mtime) clip = hit.Clip;
                else
                {
                    if (!File.Exists(path)) throw new FileNotFoundException(T("文件不存在"), path);
                    clip = AudioDecode.FromFile(path);
                    if (_files.Count > 32) _files.Clear();
                    _files[path] = (mtime, clip);
                }
            }
            Player.PlaySound(id, clip);
        }
        catch (Exception e)
        {
            Failed?.Invoke(T("无法播放音频：{0}", e.Message));
        }
    });

    /// <summary>引擎就绪后把文字常用语先合成好，按快捷键时直接播放。</summary>
    public void Preload()
    {
        foreach (var p in C["phrases"]!.AsArray().OfType<JsonObject>())
        {
            var t = p["text"]?.GetValue<string>()?.Trim() ?? "";
            if (t.Length > 0 && string.IsNullOrEmpty(p["audio"]?.GetValue<string>()))
                _jobs.Writer.TryWrite(new Job(-1, t, true, false));
        }
    }

    private async Task WorkerAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync())
        {
            if (job.Gen >= 0 && job.Gen != _gen) continue;
            var cfg = C.DeepClone().AsObject(); // 合成期间界面改配置不影响这一句
            var eng = Current;
            var key = eng.CacheKey(cfg) + "|" + job.Text;
            Clip? clip;
            lock (_cache) _cache.TryGetValue(key, out clip);
            if (clip == null)
            {
                if (!eng.Ready && eng.Loading && job.Play) SetBusy("等待加载"); // 翻译键
                while (!eng.Ready && eng.Loading) await Task.Delay(200);
                if (!eng.Ready)
                {
                    if (job.Play) { SetBusy(""); Failed?.Invoke(eng.Error.Length > 0 ? eng.Error : T("{0} 未就绪", eng.Label)); }
                    continue;
                }
                try
                {
                    if (job.Play) SetBusy("合成中"); // 翻译键
                    var t0 = DateTime.UtcNow;
                    clip = await eng.SynthAsync(job.Text, cfg, CancellationToken.None);
                    Log.Info(T("合成 {0:0.00}s：{1}", (DateTime.UtcNow - t0).TotalSeconds, job.Text.Length > 40 ? job.Text[..40] : job.Text));
                }
                catch (Exception e)
                {
                    if (job.Play) SetBusy("");
                    Log.Error(T("合成失败：{0}", e.Message));
                    if (job.Play) Failed?.Invoke(e.Message);
                    continue;
                }
                if (job.Cache)
                {
                    lock (_cache)
                    {
                        _cache[key] = clip;
                        _cacheOrder.AddLast(key);
                        while (_cacheOrder.Count > 128)
                        {
                            _cache.Remove(_cacheOrder.First!.Value);
                            _cacheOrder.RemoveFirst();
                        }
                    }
                }
            }
            if (job.Play && (job.Gen < 0 || job.Gen == _gen)) Player.Play(clip, job.Tag);
            else if (job.Play) SetBusy("");
        }
    }

    public void Dispose()
    {
        Player.Dispose();
        Gsv.Dispose();
    }
}
