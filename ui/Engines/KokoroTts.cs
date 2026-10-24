using System.Text.Json.Nodes;
using SherpaOnnx;
using ViaSonic.Asr;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

/// <summary>
/// Kokoro 本地离线合成（sherpa-onnx，CPU）：100 个中文音色和 3 个英文音色，中英混读。
/// 模型由用户点「下载」，选中这个引擎时加载，切到别的引擎就释放。
/// </summary>
public sealed class KokoroTts : ITtsEngine, IDisposable
{
    /// <summary>默认音色：女声 01。</summary>
    public const string DefaultVoice = "3";

    private readonly object _lock = new();
    private OfflineTts? _tts;
    private volatile bool _loading;
    /// <summary>加载期间又切走了：加载完直接释放。</summary>
    private volatile bool _wanted;
    private string _error = "";
    private CancellationTokenSource? _downloadCts;

    public string Key => "kokoro";
    public string Label => "Kokoro";
    public bool Ready => _tts != null;
    public bool Loading => _loading;
    public string Error => KokoroModel.Ready ? _error : T("语音模型未下载");
    public string ErrorKind => "";
    public string Missing(JsonObject c) => KokoroModel.Ready ? "" : T("未下载");

    /// <summary>下载进度 0~1，null＝没在下载。</summary>
    public double? Download { get; private set; }
    public string DownloadError { get; private set; } = "";

    /// <summary>加载或下载状态变了（可能在任意线程触发）。</summary>
    public event Action? Changed;

    public string CacheKey(JsonObject c) => $"kokoro|{c["kokoro_voice"]}|{c["kokoro_speed"]}";

    /// <summary>
    /// 下拉框里的音色：ID 是模型里的说话人编号。0~2 是英文音色，3~57 是中文女声，58~102 是中文男声。
    /// 官方只给了编号没有名字，按顺序叫「女声 01」「男声 01」。
    /// </summary>
    public static List<(string Id, string Name)> Voices()
    {
        var list = new List<(string Id, string Name)>();
        for (int i = 0; i < 55; i++) list.Add(($"{3 + i}", T("女声 {0}", (i + 1).ToString("00"))));
        for (int i = 0; i < 45; i++) list.Add(($"{58 + i}", T("男声 {0}", (i + 1).ToString("00"))));
        list.Add(("0", T("英语女声 1")));
        list.Add(("1", T("英语女声 2")));
        list.Add(("2", T("英式女声")));
        return list;
    }

    public void Load(JsonObject cfg)
    {
        lock (_lock)
        {
            _wanted = true;
            if (_tts != null || _loading || !KokoroModel.Ready) return;
            _loading = true;
            _error = "";
        }
        Changed?.Invoke();
        _ = Task.Run(LoadNow);
    }

    private void LoadNow()
    {
        try
        {
            var cfg = new OfflineTtsConfig();
            cfg.Model.Kokoro.Model = NativePath.Short(KokoroModel.File("model.int8.onnx"));
            cfg.Model.Kokoro.Voices = NativePath.Short(KokoroModel.File("voices.bin"));
            cfg.Model.Kokoro.Tokens = NativePath.Short(KokoroModel.File("tokens.txt"));
            cfg.Model.Kokoro.DataDir = NativePath.Short(KokoroModel.File("espeak-ng-data"));
            cfg.Model.Kokoro.Lexicon = string.Join(",", new[] { "lexicon-us-en.txt", "lexicon-zh.txt" }.Select(f => NativePath.Short(KokoroModel.File(f))));
            // 数字、日期、电话号码按中文读法
            cfg.RuleFsts = string.Join(",", new[] { "date-zh.fst", "phone-zh.fst", "number-zh.fst" }.Select(f => NativePath.Short(KokoroModel.File(f))));
            cfg.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            cfg.Model.Provider = "cpu";
            var tts = new OfflineTts(cfg);
            lock (_lock)
            {
                if (_wanted) _tts = tts;
                else tts.Dispose();
            }
        }
        catch (Exception e)
        {
            _error = T("Kokoro 加载失败：{0}", e.Message);
            Log.Error(_error, e);
        }
        _loading = false;
        Changed?.Invoke();
    }

    /// <summary>切到别的引擎时释放内存。</summary>
    public void Unload()
    {
        lock (_lock)
        {
            _wanted = false;
            _tts?.Dispose();
            _tts = null;
        }
    }

    public Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token) => Task.Run(() =>
    {
        lock (_lock)
        {
            if (_tts == null) throw new TtsException(Error.Length > 0 ? Error : T("{0} 未就绪", Label));
            int sid = int.TryParse(c.Str("kokoro_voice"), out var v) && v >= 0 && v < _tts.NumSpeakers ? v : int.Parse(DefaultVoice);
            var audio = _tts.Generate(text, (float)Math.Clamp(c.Num("kokoro_speed"), 0.5, 2.0), sid);
            try
            {
                var samples = audio.Samples;
                if (samples.Length == 0) throw new TtsException(T("{0} 没有生成声音，所选音色可能读不了这种语言", Label));
                return new Clip(samples, audio.SampleRate);
            }
            finally
            {
                audio.Dispose(); // 有 Dispose 但没实现 IDisposable
            }
        }
    }, token);

    public async Task DownloadModelAsync()
    {
        if (Download != null) return;
        _downloadCts = new CancellationTokenSource();
        Download = 0;
        DownloadError = "";
        Changed?.Invoke();
        var last = 0.0;
        var progress = new Progress<double>(p =>
        {
            Download = p;
            if (p - last >= 0.01) { last = p; Changed?.Invoke(); }
        });
        try
        {
            await KokoroModel.DownloadAsync(progress, _downloadCts.Token);
            Log.Info(T("Kokoro 语音模型下载完成"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DownloadError = T("下载失败：{0}", e.Message);
            Log.Error(DownloadError);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Download = null;
            _downloadCts = null;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _downloadCts?.Cancel();
        Unload();
    }
}

/// <summary>Kokoro v1.1 中文版模型（sherpa-onnx 转换的 int8 版）：点了「下载」才下，放在数据目录的 models 下。</summary>
public static class KokoroModel
{
    private const string Repo = "csukuangfj/kokoro-int8-multi-lang-v1_1";

    public static string Dir => Path.Combine(AppPaths.DataDir, "models", "kokoro");
    public static string File(string name) => Path.Combine(Dir, name.Replace('/', Path.DirectorySeparatorChar));
    /// <summary>全部文件下完才写这个标记，中途失败不会被当成下好了。</summary>
    private static string DoneFile => File(".done");
    public static bool Ready => System.IO.File.Exists(DoneFile);

    /// <summary>
    /// 仓库里要下的文件：英文词典只用美式（英式女声也能读），jieba 词典 Kokoro 用不到；
    /// espeak-ng-data 只用来读词典里没有的英文词，其他语言的词典（俄语一个就 8 MB）和变声音色都不用。
    /// </summary>
    internal static bool Needed(string path) =>
        path is "model.int8.onnx" or "voices.bin" or "tokens.txt" or "lexicon-us-en.txt" or "lexicon-zh.txt"
                or "date-zh.fst" or "number-zh.fst" or "phone-zh.fst"
        || (path.StartsWith("espeak-ng-data/", StringComparison.Ordinal) && !path.StartsWith("espeak-ng-data/voices/", StringComparison.Ordinal)
            && (!path.EndsWith("_dict", StringComparison.Ordinal) || path == "espeak-ng-data/en_dict"));

    /// <summary>下载模型，progress 是 0~1。下好的文件大小对得上就跳过，失败后再点下载会接着下。</summary>
    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken token)
    {
        var (list, mirror) = await HfMirror.FastestAsync($"api/models/{Repo}/tree/main?recursive=true", token);
        JsonArray files;
        using (list) files = JsonNode.Parse(await list.Content.ReadAsStringAsync(token))!.AsArray();
        var need = files.OfType<JsonObject>().Where(f => f.Str("type") == "file" && Needed(f.Str("path")))
            .Select(f => (Path: f.Str("path"), Size: (long)f.Num("size"))).ToList();
        if (!need.Any(f => f.Path == "model.int8.onnx")) throw new IOException(T("下载不完整"));

        long total = need.Sum(f => f.Size), done = 0;
        void Add(long n)
        {
            if (total > 0) progress.Report((double)Interlocked.Add(ref done, n) / total);
        }
        // 小文件很多（两百多个），同时下几个；一个失败就停下其他的，报这个失败的原因
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var gate = new SemaphoreSlim(6);
        Exception? failed = null;
        try
        {
            await Task.WhenAll(need.Select(async f =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    var file = File(f.Path);
                    if (System.IO.File.Exists(file) && new FileInfo(file).Length == f.Size)
                    {
                        Add(f.Size);
                        return;
                    }
                    var url = mirror + Repo + "/resolve/main/" + string.Join("/", f.Path.Split('/').Select(Uri.EscapeDataString));
                    using var r = await HfMirror.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    r.EnsureSuccessStatusCode();
                    await HfMirror.SaveAsync(r, file, Add, cts.Token);
                }
                catch (Exception e) when (e is not OperationCanceledException || !cts.IsCancellationRequested)
                {
                    failed ??= e;
                    cts.Cancel();
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch when (failed != null && !token.IsCancellationRequested)
        {
            throw failed;
        }
        await System.IO.File.WriteAllTextAsync(DoneFile, "", token);
    }
}
