using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SherpaOnnx;

namespace ViaSonic.Asr;

/// <summary>本地识别：SenseVoice（中英日韩粤，带标点），用 CPU 跑，模型由用户在卡片上点「下载」。</summary>
public sealed class LocalAsr : IRecognizer, IDisposable
{
    private readonly object _lock = new();
    private OfflineRecognizer? _rec;
    private Task? _loading;

    public string Key => "local";
    public string Label => T("本地模型");
    public bool Loading => _loading is { IsCompleted: false };
    public string Error { get; private set; } = "";

    /// <summary>加载状态变了。</summary>
    public event Action? Changed;

    public string Missing(JsonObject c) => AsrModel.Ready ? "" : T("未下载");

    /// <summary>模型已下载但还没加载时在后台加载（约 1~2 秒）。</summary>
    public void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_rec != null || Loading || !AsrModel.Ready) return;
            Error = "";
            _loading = Task.Run(Load);
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            var cfg = new OfflineRecognizerConfig();
            cfg.ModelConfig.SenseVoice.Model = NativePath.Short(AsrModel.ModelFile);
            cfg.ModelConfig.SenseVoice.Language = "auto";
            cfg.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
            cfg.ModelConfig.Tokens = NativePath.Short(AsrModel.TokensFile);
            cfg.ModelConfig.NumThreads = 2;
            var rec = new OfflineRecognizer(cfg);
            lock (_lock) _rec = rec;
        }
        catch (Exception e)
        {
            Error = T("识别失败：{0}", e.Message);
            Log.Error(Error, e);
        }
        Changed?.Invoke();
    }

    /// <summary>关掉语音输入或换成在线识别时释放内存。</summary>
    public void Unload()
    {
        lock (_lock)
        {
            _rec?.Dispose();
            _rec = null;
        }
    }

    public async Task<string> RecognizeAsync(float[] pcm, JsonObject c, CancellationToken token)
    {
        EnsureLoaded();
        if (_loading is { } t) await t;
        return await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_rec == null) throw new AsrException(Error.Length > 0 ? Error : T("未下载"));
                using var s = _rec.CreateStream();
                s.AcceptWaveform(Pcm.Rate, pcm);
                _rec.Decode(s);
                return s.Result.Text;
            }
        }, token);
    }

    public void Dispose() => Unload();
}

/// <summary>SenseVoice 模型文件：点了「下载」才下，放在数据目录的 models 下。</summary>
public static class AsrModel
{
    private const string Repo = "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/resolve/main/";

    public static string Dir => Path.Combine(AppPaths.DataDir, "models", "sense-voice");
    public static string ModelFile => Path.Combine(Dir, "model.int8.onnx");
    public static string TokensFile => Path.Combine(Dir, "tokens.txt");
    public static bool Ready => File.Exists(ModelFile) && File.Exists(TokensFile);

    /// <summary>下载模型，progress 是 0~1。先写到 .part，下完再改名，中途失败不会留下半个模型。</summary>
    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken token)
    {
        Directory.CreateDirectory(Dir);
        var (model, mirror) = await HfMirror.FastestAsync(Repo + "model.int8.onnx", token);
        using var _ = model;
        // tokens.txt 很小，从同一边下
        using (var tokens = await HfMirror.Http.GetAsync(mirror + Repo + "tokens.txt", token))
        {
            tokens.EnsureSuccessStatusCode();
            await File.WriteAllBytesAsync(TokensFile + ".part", await tokens.Content.ReadAsByteArrayAsync(token), token);
        }
        long total = model.Content.Headers.ContentLength ?? 0, done = 0;
        await HfMirror.SaveAsync(model, ModelFile, n =>
        {
            done += n;
            if (total > 0) progress.Report((double)done / total);
        }, token);
        File.Move(TokensFile + ".part", TokensFile, true);
    }
}

/// <summary>sherpa-onnx 用 ANSI 字符串接收路径，含中文等字符的路径换成 8.3 短路径。</summary>
internal static partial class NativePath
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string longPath, [Out] char[] shortPath, uint size);

    public static string Short(string path)
    {
        if (path.All(ch => ch < 128)) return path;
        var buf = new char[1024];
        uint n = GetShortPathName(path, buf, (uint)buf.Length);
        return n > 0 && n < buf.Length ? new string(buf, 0, (int)n) : path;
    }
}
