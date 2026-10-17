using System.Text.Json.Nodes;
using System.Threading.Channels;
using ViaSonic.Audio;
using ViaSonic.Engines;

namespace ViaSonic;

/// <summary>合成调度：单线程合成队列（避免 GPU 并发）、常用语预生成缓存、播放。</summary>
public sealed class Speaker : IDisposable
{
    /// <summary>Sound：合成后作为音效播放（循环的文字常用语），Tag 是常用语 id。</summary>
    /// <summary>Export：只合成不播放，结果交给导出（不受打断影响）。</summary>
    /// <summary>Random：随机音色，合成前现选引擎和音色。</summary>
    private sealed record Job(int Gen, string Text, bool Cache, bool Play, string Tag = "", bool Sound = false,
                              TaskCompletionSource<Clip>? Export = null, bool Random = false);

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
    /// <summary>上一句朗读的生成耗时（毫秒），用缓存直接播放的不算；0＝还没合成过。</summary>
    public int LastSynthMs { get; private set; }

    /// <summary>状态有变化（可能在任意线程触发）。</summary>
    public event Action? Changed;
    public event Action<string>? Failed;

    public Speaker(Func<JsonObject> cfg)
    {
        _cfg = cfg;
        Engines = new()
        {
            ["gsv"] = Gsv, ["edge"] = new EdgeTts(), ["sapi"] = new SystemTts(),
            ["azure"] = new AzureTts(cfg), ["fish"] = new FishAudio(cfg), ["doubao"] = new Doubao(cfg),
            ["bailian"] = new Bailian(cfg), ["minimax"] = new MiniMax(cfg), ["custom"] = new CustomApi(cfg),
        };
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
            var s = e is OnlineEngine { Ready: false } o ? o.Missing(C) : e.Ready ? T("就绪") : e.Loading ? T("加载中") : T("未加载");
            return $"{e.Label} {s}";
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
        // 随机音色只换朗读的句子，常用语不变
        bool random = tag.Length == 0 && (C["random_voice"]?.GetValue<bool>() ?? false);
        _jobs.Writer.TryWrite(new Job(_gen, text, cache && !random, true, tag, Random: random));
    }

    /// <summary>开了循环的文字常用语：合成后像音效一样叠加、循环播放，不打断也不排队。</summary>
    public void SpeakAsSound(string id, string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length > 0) _jobs.Writer.TryWrite(new Job(_gen, text, true, true, id, Sound: true));
    }

    /// <summary>用当前引擎合成（有缓存直接用），给常用语导出音频。</summary>
    public Task<Clip> SynthAsync(string text)
    {
        var tcs = new TaskCompletionSource<Clip>(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Writer.TryWrite(new Job(-1, text.Trim(), true, false, Export: tcs));
        return tcs.Task;
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
    /// <param name="cache">播放列表的歌曲很长，不放进缓存。</param>
    public void PlaySound(string id, string path, Action<bool>? ended = null, bool cache = true, double fadeIn = 0, double fadeTail = 0,
        Func<double>? speed = null)
    {
        // 预先解码的下一首在这里就领走：播放列表紧接着会预解码再下一首，把它换掉
        (string Path, DateTime Mtime, Task<Clip> Clip)? pre = null;
        lock (_files)
            if (_prefetch is { } pf && pf.Path == path)
            {
                pre = pf;
                _prefetch = null;
            }
        _ = Task.Run(() => PlaySoundNow(id, path, pre, ended, cache, fadeIn, fadeTail, speed));
    }

    private void PlaySoundNow(string id, string path, (string Path, DateTime Mtime, Task<Clip> Clip)? pre, Action<bool>? ended, bool cache,
        double fadeIn, double fadeTail, Func<double>? speed)
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            Clip? clip = null;
            if (pre is { } p && p.Mtime == mtime) clip = p.Clip.Result; // 还在解码就等它，不重复解码
            else
                lock (_files)
                    if (_files.TryGetValue(path, out var hit) && hit.Mtime == mtime) clip = hit.Clip;
            // 解码不在锁里做：长歌要 1 秒以上，不能挡住同时按下的音效
            if (clip == null)
            {
                clip = Decode(path);
                if (cache)
                    lock (_files)
                    {
                        if (_files.Count > 32) _files.Clear();
                        _files[path] = (mtime, clip);
                    }
            }
            Player.PlaySound(id, clip, ended: ended, fadeIn: fadeIn, fadeTail: fadeTail, speed: speed);
        }
        catch (Exception e)
        {
            Failed?.Invoke(T("无法播放音频：{0}", e.InnerException?.Message ?? e.Message));
            ended?.Invoke(false);
        }
    }

    private (string Path, DateTime Mtime, Task<Clip> Clip)? _prefetch;

    /// <summary>
    /// 播放列表的下一首提前在后台解码，到时候直接播放：现解码一首歌要零点几秒到 1 秒多，电脑忙（开着游戏）时要好几秒，
    /// 切歌和交叉过渡就会晚开始、中间断一下。只留一首。
    /// </summary>
    public void Prefetch(string? path)
    {
        lock (_files)
        {
            if (path == null)
            {
                _prefetch = null;
                return;
            }
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(path); }
            catch { return; }
            if (_prefetch is { } pf && pf.Path == path && pf.Mtime == mtime) return;
            var task = Task.Run(() => Decode(path));
            task.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted); // 解码失败到播放时再报
            _prefetch = (path, mtime, task);
        }
    }

    private static Clip Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(T("文件不存在"), path);
        return AudioDecode.FromFile(path);
    }

    /// <summary>引擎就绪后把文字常用语先合成好，按快捷键时直接播放。</summary>
    public void Preload()
    {
        if (Current is OnlineEngine { Paid: true }) return; // 收费服务第一次用到时再合成
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
            if (job.Random && await RandomVoice.PickAsync(cfg, job.Text, Engines) is { } pick)
            {
                eng = pick.Engine;
                cfg = pick.Cfg;
            }
            var key = eng.CacheKey(cfg) + "|" + job.Text;
            Clip? clip;
            lock (_cache) _cache.TryGetValue(key, out clip);
            bool show = job.Play || job.Export != null; // 状态栏显示等待 / 合成中
            if (clip == null)
            {
                if (!eng.Ready && eng.Loading && show) SetBusy("等待加载"); // 翻译键
                while (!eng.Ready && eng.Loading) await Task.Delay(200);
                if (!eng.Ready)
                {
                    var why = eng.Error.Length > 0 ? eng.Error : T("{0} 未就绪", eng.Label);
                    if (show) SetBusy("");
                    if (job.Export != null) job.Export.TrySetException(new TtsException(why));
                    else if (job.Play) Failed?.Invoke(why);
                    continue;
                }
                try
                {
                    if (show) SetBusy("合成中"); // 翻译键
                    var t0 = DateTime.UtcNow;
                    clip = await eng.SynthAsync(job.Text, cfg, CancellationToken.None);
                    Log.Info(T("合成 {0:0.00}s：{1}", (DateTime.UtcNow - t0).TotalSeconds, job.Text.Length > 40 ? job.Text[..40] : job.Text));
                    if (job.Play) LastSynthMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds; // 状态栏随后会刷新
                }
                catch (Exception e)
                {
                    if (show) SetBusy("");
                    Log.Error(T("合成失败：{0}", e.Message));
                    if (job.Export != null) job.Export.TrySetException(e);
                    else if (job.Play) Failed?.Invoke(e.Message);
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
            if (job.Export != null)
            {
                if (Player.Playing) SetBusy("播放中"); // 翻译键
                else SetBusy("");
                job.Export.TrySetResult(clip);
                continue;
            }
            if (job.Play && Player.SpeechFx.On.Count > 0)
            {
                try { clip = VoiceFx.ProcessClip(clip, Player.SpeechFx); } // 缓存里留原声，换效果后不用重新合成
                catch (Exception e) { Log.Error(T("播放失败"), e); }
            }
            if (job.Play && job.Sound && job.Gen == _gen) Player.PlaySound(job.Tag, clip, speech: true);
            else if (job.Play && (job.Gen < 0 || job.Gen == _gen)) Player.Play(clip, job.Tag);
            else if (job.Play) SetBusy("");
        }
    }

    public void Dispose()
    {
        Player.Dispose();
        Gsv.Dispose();
    }
}
