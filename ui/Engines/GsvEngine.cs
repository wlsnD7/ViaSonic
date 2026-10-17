using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

/// <summary>
/// GPT-SoVITS：在整合包自带的 Python 里启动 gsv_server.py，只在选用这个引擎时才启动。
/// 模型在配置里管理，切换模型只换权重。
/// </summary>
public sealed class GsvEngine : ITtsEngine, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private Process? _proc;
    private string? _home, _device;
    private int _logSeq;
    private Task? _starting;

    public string Key => "gsv";
    public string Label => "GPT-SoVITS";
    public bool Ready { get; private set; }
    public bool Loading { get; private set; }
    public string Error { get; private set; } = "";
    public string ErrorKind { get; private set; } = "";
    public event Action? Changed;

    public static JsonObject? CurrentModel(JsonObject c)
    {
        var id = c["gsv_model"]?.GetValue<string>();
        return c["gsv_models"]?.AsArray().FirstOrDefault(m => m?["id"]?.GetValue<string>() == id)?.AsObject();
    }

    public string CacheKey(JsonObject c)
    {
        var m = CurrentModel(c);
        return $"gsv|{m?["gpt"]}|{m?["sovits"]}|{m?["ref"]}|{m?["ref_text"]}|{m?["ref_lang"]}|{c["gsv_speed"]}|{c["gsv_text_lang"]}";
    }

    // ------------------------------------------------------------------ 查找整合包
    public static bool IsGsvHome(string? dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "runtime", "python.exe"))
                                   && Directory.Exists(Path.Combine(dir, "GPT_SoVITS"));

    /// <summary>配置里的目录 → GSV_HOME 环境变量 → ViaSonic 旁边的 GPT-SoVITS 文件夹。</summary>
    public static string? FindHome(string configured)
    {
        foreach (var d in new[] { configured, Environment.GetEnvironmentVariable("GSV_HOME") })
            if (IsGsvHome(d)) return d;
        foreach (var start in new[] { AppPaths.ScriptDir, AppContext.BaseDirectory })
        {
            var parent = start == null ? null : Directory.GetParent(start.TrimEnd('\\'))?.FullName;
            if (parent == null) continue;
            foreach (var root in new[] { Path.Combine(parent, "GPT-SoVITS"), parent })
            {
                if (!Directory.Exists(root)) continue;
                if (IsGsvHome(root)) return root;
                foreach (var d in Directory.GetDirectories(root).Order())
                    if (IsGsvHome(d)) return d;
            }
        }
        return null;
    }

    private static string? CheckModel(JsonObject? m)
    {
        if (m == null) return T("还没有模型，点「模型」右边的 + 添加");
        var names = new (string Key, string Name)[] { ("gpt", "GPT 权重"), ("sovits", "SoVITS 权重"), ("ref", "参考音频") }; // 翻译键
        var missing = names.Where(x => !File.Exists(m[x.Key]?.GetValue<string>() ?? "")).Select(x => T(x.Name)).ToList();
        return missing.Count == 0 ? null : T("「{0}」缺少{1}", m["name"], string.Join(T("、"), missing));
    }

    /// <summary>切换语言后，把还显示着的「找不到整合包 / 模型文件不全」重新生成一遍。只检查文件，不加载。</summary>
    public void RefreshError(JsonObject c)
    {
        if (Loading || ErrorKind is not ("gsv_missing" or "model_missing")) return;
        if (FindHome(c["gsv_home"]?.GetValue<string>() ?? "") == null)
            SetState(false, false, T("找不到 GPT-SoVITS"), "gsv_missing");
        else if (CheckModel(CurrentModel(c)) is { } bad)
            SetState(Ready, false, bad, "model_missing");
    }

    private int _gen; // 每次 Unload 加一，旧的加载任务发现变了就不再更新状态

    /// <summary>切到别的引擎且没开「切换引擎后保持加载」时：关掉服务，释放显存和内存。</summary>
    public void Unload()
    {
        Interlocked.Increment(ref _gen);
        StopProcess();
        SetState(false, false);
    }

    private void SetState(bool ready, bool loading, string error = "", string kind = "")
    {
        Ready = ready;
        Loading = loading;
        Error = error;
        ErrorKind = kind;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ 加载 / 切换模型
    /// <summary>加载引擎，或在已加载时切换到配置里当前的模型。非阻塞。</summary>
    public void Load(JsonObject c)
    {
        var home = FindHome(c["gsv_home"]?.GetValue<string>() ?? "");
        if (home == null)
        {
            SetState(false, false, T("找不到 GPT-SoVITS"), "gsv_missing");
            return;
        }
        var model = CurrentModel(c);
        var bad = CheckModel(model);
        if (bad != null)
        {
            SetState(Ready, false, bad, "model_missing");
            return;
        }
        var device = c["gsv_device"]?.GetValue<string>() ?? "cuda";
        if (_proc != null && (_home != home || _device != device)) StopProcess(); // 换了整合包或设备，重启服务
        SetState(Ready, true);
        var body = new JsonObject { ["model"] = model!.DeepClone(), ["lang"] = Lang.Current };
        int gen = Volatile.Read(ref _gen);
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureProcessAsync(home, device);
                if (gen != Volatile.Read(ref _gen))
                {
                    StopProcess(); // 启动期间已经切走了
                    return;
                }
                await PostAsync("load", body);
                await PollUntilSettledAsync(gen);
            }
            catch (Exception e)
            {
                if (gen != Volatile.Read(ref _gen)) return; // 切走时关掉了服务，不算失败
                Log.Error(T("GPT-SoVITS 启动失败"), e);
                SetState(false, false, T("GPT-SoVITS 启动失败：{0}", e.Message));
            }
        });
    }

    private async Task EnsureProcessAsync(string home, string device)
    {
        if (_proc is { HasExited: false }) return;
        if (_starting != null)
        {
            await _starting;
            return;
        }
        _starting = StartProcessAsync(home, device);
        try { await _starting; }
        finally { _starting = null; }
    }

    private async Task StartProcessAsync(string home, string device)
    {
        var script = AppPaths.ScriptDir == null ? null : Path.Combine(AppPaths.ScriptDir, "gsv_server.py");
        if (script == null || !File.Exists(script)) throw new InvalidOperationException(T("找不到 gsv_server.py"));
        int port = FreePort();
        bool debug = Environment.GetCommandLineArgs().Contains("--debug");
        var psi = new ProcessStartInfo(Path.Combine(home, "runtime", debug ? "python.exe" : "pythonw.exe"))
        {
            WorkingDirectory = home,
            UseShellExecute = false,
            CreateNoWindow = !debug,
        };
        foreach (var a in new[] { "-I", script, "--port", port.ToString(), "--home", home, "--device", device,
                                  "--parent-pid", Environment.ProcessId.ToString(), "--log", Path.Combine(AppPaths.LogDir, "gsv.log"),
                                  "--lang", Lang.Current })
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        _proc = Process.Start(psi) ?? throw new InvalidOperationException(T("无法启动 Python"));
        _home = home;
        _device = device;
        _logSeq = 0;
        _http.CancelPendingRequests();
        _base = $"http://127.0.0.1:{port}/";
        Log.Info(T("启动 GPT-SoVITS 服务：{0}", home));

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_proc.HasExited) throw new InvalidOperationException(T("Python 退出（{0}），见 logs/gsv.log", _proc.ExitCode));
            try
            {
                using var r = await _http.GetAsync(_base + "status");
                if (r.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(300);
        }
        throw new TimeoutException(T("GPT-SoVITS 服务启动超时"));
    }

    private string _base = "";

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, JsonObject body, CancellationToken token = default) =>
        await _http.PostAsync(_base + path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), token);

    private async Task PollUntilSettledAsync(int gen)
    {
        var lastOk = DateTime.UtcNow;
        while (_proc is { HasExited: false })
        {
            if (gen != Volatile.Read(ref _gen)) return;
            await PullLogsAsync();
            JsonNode s;
            try
            {
                s = JsonNode.Parse(await _http.GetStringAsync(_base + $"status?lang={Lang.Current}"))!;
                lastOk = DateTime.UtcNow;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // 首次加载模型时服务很忙，偶尔查不到状态；进程还在就继续等，连续一分钟查不到才算失败
                if (DateTime.UtcNow - lastOk > TimeSpan.FromSeconds(60)) throw;
                await Task.Delay(1000);
                continue;
            }
            bool loading = s["loading"]!.GetValue<bool>();
            SetState(s["ready"]!.GetValue<bool>(), loading, s["error"]!.GetValue<string>(), s["kind"]!.GetValue<string>());
            if (!loading) return;
            await Task.Delay(500);
        }
        if (gen != Volatile.Read(ref _gen)) return;
        SetState(false, false, T("GPT-SoVITS 服务已退出，见 logs/gsv.log"));
    }

    private async Task PullLogsAsync()
    {
        try
        {
            var rows = JsonNode.Parse(await _http.GetStringAsync(_base + $"logs?after={_logSeq}&lang={Lang.Current}"))!.AsArray();
            foreach (var r in rows)
            {
                Log.Raw(r!["text"]!.GetValue<string>());
                _logSeq = r["seq"]!.GetValue<int>();
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ 合成
    public async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        if (!Ready || _proc is not { HasExited: false }) throw new TtsException(T("GPT-SoVITS 未就绪"));
        var body = new JsonObject
        {
            ["text"] = text,
            ["model"] = CurrentModel(c)?.DeepClone(),
            ["speed"] = App.ToDouble(c["gsv_speed"]),
            ["text_lang"] = c["gsv_text_lang"]?.GetValue<string>() ?? "zh",
            ["lang"] = Lang.Current,
        };
        using var r = await PostAsync("synth", body, token);
        if (!r.IsSuccessStatusCode)
        {
            var err = JsonNode.Parse(await r.Content.ReadAsStringAsync(token));
            await PullLogsAsync();
            throw new TtsException(err?["error"]?.GetValue<string>() ?? T("合成失败"), err?["kind"]?.GetValue<string>() ?? "");
        }
        var wav = await r.Content.ReadAsByteArrayAsync(token);
        _ = PullLogsAsync();
        return AudioDecode.FromWave(wav);
    }

    private void StopProcess()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc = null;
        Ready = false;
    }

    public void Dispose()
    {
        StopProcess();
        _http.Dispose();
    }
}
