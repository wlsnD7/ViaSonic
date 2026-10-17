using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json.Nodes;
using NAudio.Wave;

namespace ViaSonic.Audio;

/// <summary>
/// RVC 实时变声：在 RVC 整合包的 Python 里跑 rvc_server.py，音频通过共享内存一块一块地送过去（协议和官方 RVCRealtimeVST 一样）。
/// 麦克风先重采样到 48kHz，攒够一块（采样长度）送去转换，转换结果排队播放；延迟约为两块加上推理时间。
/// </summary>
public sealed class RvcHost : IDisposable
{
    public const int Rate = 48000;

    /// <summary>需要重启进程的设置：整合包、模型、索引、块长度、淡入淡出、额外推理时长。</summary>
    public sealed record Options(string Root, string Model, string Index, int BlockMs, int CrossfadeMs, int ExtraMs);

    /// <summary>运行中随时可改的参数。</summary>
    public sealed record Params(float Pitch, float Formant, float IndexRate, float RmsMix, float Gate, int F0);

    private readonly object _lock = new();
    private Options? _want;
    private Worker? _worker;

    public volatile Params Live = new(0, 0, 0, 0.25f, -60, 0);

    /// <summary>状态变化（任意线程）。</summary>
    public event Action? Changed;

    public bool Loading => _worker?.State == WorkerState.Loading;
    public bool Ready => _worker?.State == WorkerState.Ready;
    public string Error => _worker?.Error ?? "";
    public int BlockMs => _want?.BlockMs ?? 0;
    public float InferMs => _worker?.InferMs ?? 0;

    /// <summary>最近 3 秒内有没有因为跟不上而丢块。</summary>
    public bool Lagging => DateTime.UtcNow - _lastDrop < TimeSpan.FromSeconds(3);
    private DateTime _lastDrop = DateTime.MinValue;

    /// <summary>设置要运行的模型；null＝停止，释放显存。同样的设置不会重启。</summary>
    public void Want(Options? o)
    {
        Worker? old = null;
        lock (_lock)
        {
            if (o == _want) return;
            _want = o;
            old = _worker;
            _worker = o == null ? null : new Worker(o, () => Changed?.Invoke());
        }
        old?.Dispose();
        Changed?.Invoke();
    }

    /// <summary>转换一块；还没加载好或出错时返回 null。</summary>
    internal float[]? Process(float[] block)
    {
        Worker? w;
        lock (_lock) w = _worker;
        return w is { State: WorkerState.Ready } && w.BlockFrames == block.Length ? w.Process(block, Live) : null;
    }

    internal int BlockFrames
    {
        get { lock (_lock) return _worker?.BlockFrames ?? 0; }
    }

    internal void Dropped()
    {
        _lastDrop = DateTime.UtcNow;
        Changed?.Invoke();
    }

    public void Dispose() => Want(null);

    // ------------------------------------------------------------------ 整合包与模型
    /// <summary>新版整合包：runtime\python.exe + infer\rtrvc.py。</summary>
    public static bool IsHome(string? dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "runtime", "python.exe")) && File.Exists(Path.Combine(dir, "infer", "rtrvc.py"));

    /// <summary>旧版整合包（没有实时推理接口）。</summary>
    public static bool IsOldHome(string? dir) =>
        !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "runtime", "python.exe")) && !IsHome(dir)
        && (Directory.Exists(Path.Combine(dir, "infer")) || File.Exists(Path.Combine(dir, "infer-web.py")));

    /// <summary>配置里的目录 → ViaSonic 旁边的 RVC 文件夹（及其子文件夹）。找到旧版时返回它，由调用方提示。</summary>
    public static string? FindHome(string configured)
    {
        if (IsHome(configured) || IsOldHome(configured)) return configured;
        string? old = null;
        foreach (var start in new[] { AppPaths.ScriptDir, AppContext.BaseDirectory })
        {
            var parent = start == null ? null : Directory.GetParent(start.TrimEnd('\\'))?.FullName;
            if (parent == null) continue;
            foreach (var root in new[] { Path.Combine(parent, "RVC"), parent })
            {
                if (!Directory.Exists(root)) continue;
                if (IsHome(root)) return root;
                foreach (var d in Directory.GetDirectories(root).Order())
                {
                    if (IsHome(d)) return d;
                    if (IsOldHome(d)) old ??= d;
                }
            }
        }
        return old;
    }

    /// <summary>整合包 assets\weights 里的模型加上自己添加的模型（完整路径）。</summary>
    public static List<string> Models(string? home, JsonArray? added)
    {
        var list = new List<string>();
        var weights = home == null ? null : Path.Combine(home, "assets", "weights");
        if (weights != null && Directory.Exists(weights)) list.AddRange(Directory.GetFiles(weights, "*.pth").Order());
        foreach (var p in added?.Select(x => x?.GetValue<string>() ?? "") ?? [])
            if (p.Length > 0 && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        return list;
    }

    /// <summary>按模型名找 .index：模型旁边 → 整合包 assets\indices → logs（含子文件夹）。找不到返回空。</summary>
    public static string FindIndex(string model, string? home)
    {
        var stem = Path.GetFileNameWithoutExtension(model);
        var dirs = new List<string> { Path.GetDirectoryName(model) ?? "" };
        if (home != null)
        {
            dirs.Add(Path.Combine(home, "assets", "indices"));
            dirs.Add(Path.Combine(home, "logs"));
            dirs.Add(Path.Combine(home, "logs", stem));
        }
        foreach (var d in dirs.Where(Directory.Exists))
        {
            var exact = Path.Combine(d, stem + ".index");
            if (File.Exists(exact)) return exact;
            var any = Directory.GetFiles(d, "*.index").FirstOrDefault(f =>
                Path.GetFileName(f).Contains(stem, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(d) == stem);
            if (any != null) return any;
        }
        return "";
    }

    // ------------------------------------------------------------------ 进程
    private enum WorkerState { Loading, Ready, Error }

    private sealed class Worker : IDisposable
    {
        private const uint Magic = 0x50564352;
        private const int HeaderBytes = 4096, MaxFrames = 131072, MapBytes = HeaderBytes + MaxFrames * 4 * 2;
        private const int StatusReady = 3, StatusError = -1, StatusStop = -2;

        private readonly Options _o;
        private readonly Action _changed;
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        private readonly EventWaitHandle _request, _response;
        private readonly string _configFile;
        private readonly object _call = new();
        private System.Diagnostics.Process? _proc;
        private uint _seq;
        private volatile bool _disposed;

        public volatile WorkerState State = WorkerState.Loading;
        public string Error = "";
        public float InferMs;
        public int BlockFrames { get; }

        public Worker(Options o, Action changed)
        {
            _o = o;
            _changed = changed;
            int zc = Rate / 100;
            BlockFrames = (int)Math.Round(o.BlockMs / 1000.0 * Rate / zc) * zc;
            var id = Guid.NewGuid().ToString("N")[..12];
            _map = MemoryMappedFile.CreateNew($"ViaSonic_RVC_{id}", MapBytes);
            _view = _map.CreateViewAccessor();
            _view.Write(0, Magic);
            _view.Write(4, 1u);
            _view.Write(8, 0);
            _request = new EventWaitHandle(false, EventResetMode.AutoReset, $"ViaSonic_RVC_{id}_req");
            _response = new EventWaitHandle(false, EventResetMode.AutoReset, $"ViaSonic_RVC_{id}_res");
            _configFile = Path.Combine(Path.GetTempPath(), $"viasonic_rvc_{id}.json");
            _ = Task.Run(() => Start(id));
        }

        private void Fail(string msg)
        {
            if (_disposed) return;
            Error = msg;
            State = WorkerState.Error;
            Log.Error(T("加载 RVC 失败：{0}", msg));
            _changed();
        }

        private void Start(string id)
        {
            try
            {
                var script = AppPaths.ScriptDir == null ? null : Path.Combine(AppPaths.ScriptDir, "rvc_server.py");
                if (script == null || !File.Exists(script)) throw new FileNotFoundException("rvc_server.py");
                File.WriteAllText(_configFile, new JsonObject
                {
                    ["rvc_root"] = _o.Root, ["model_path"] = _o.Model, ["index_path"] = _o.Index, ["sample_rate"] = Rate,
                    ["block_ms"] = _o.BlockMs, ["crossfade_ms"] = _o.CrossfadeMs, ["extra_ms"] = _o.ExtraMs,
                }.ToJsonString(), new UTF8Encoding(false));
                bool debug = Environment.GetCommandLineArgs().Contains("--debug");
                var runtime = Path.Combine(_o.Root, "runtime");
                var psi = new ProcessStartInfo(Path.Combine(runtime, debug ? "python.exe" : "pythonw.exe"))
                {
                    WorkingDirectory = _o.Root,
                    UseShellExecute = false,
                    CreateNoWindow = !debug,
                };
                foreach (var a in new[] { "-I", script, "--map", $"ViaSonic_RVC_{id}", "--request", $"ViaSonic_RVC_{id}_req",
                                          "--response", $"ViaSonic_RVC_{id}_res", "--config", _configFile,
                                          "--parent-pid", Environment.ProcessId.ToString() })
                    psi.ArgumentList.Add(a);
                psi.Environment["PATH"] = runtime + ";" + psi.Environment["PATH"];
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                _proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException(T("无法启动 Python"));
                Log.Info($"RVC: {Path.GetFileName(_o.Model)}");

                // 等加载完成（第一次要把模型、HuBERT、RMVPE 读进显存）
                while (!_disposed)
                {
                    int status = _view.ReadInt32(8);
                    if (status == StatusReady)
                    {
                        State = WorkerState.Ready;
                        _changed();
                        break;
                    }
                    if (status == StatusError || _proc.HasExited)
                    {
                        Fail(StatusText() is { Length: > 0 } t ? t : T("Python 退出（{0}）", _proc.HasExited ? _proc.ExitCode : -1));
                        return;
                    }
                    Thread.Sleep(200);
                }
            }
            catch (Exception e)
            {
                Fail(e.Message);
            }
        }

        private string StatusText()
        {
            var bytes = new byte[512];
            _view.ReadArray(128, bytes, 0, bytes.Length);
            int n = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, n < 0 ? bytes.Length : n).Trim();
        }

        /// <summary>同步转换一块，超时（2 秒）或出错返回 null。</summary>
        public float[]? Process(float[] block, Params p)
        {
            lock (_call)
            {
                if (_disposed || State != WorkerState.Ready) return null;
                _view.WriteArray(HeaderBytes, block, 0, block.Length);
                _view.Write(20, (uint)block.Length);
                _view.Write(32, p.Pitch);
                _view.Write(36, p.Formant);
                _view.Write(40, p.IndexRate);
                _view.Write(44, p.RmsMix);
                _view.Write(48, p.Gate);
                _view.Write(64, (uint)p.F0);
                _view.Write(12, ++_seq);
                _request.Set();
                if (!_response.WaitOne(2000) || _view.ReadUInt32(16) != _seq)
                {
                    if (_view.ReadInt32(8) == StatusError || _proc is { HasExited: true }) Fail(StatusText());
                    return null;
                }
                var output = new float[block.Length];
                _view.ReadArray(HeaderBytes + MaxFrames * 4, output, 0, output.Length);
                InferMs = _view.ReadSingle(56);
                return output;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var proc = _proc;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_call)
                {
                    try
                    {
                        _view.Write(8, StatusStop);
                        _request.Set();
                        if (proc != null && !proc.WaitForExit(1500)) proc.Kill(true);
                    }
                    catch { }
                    _view.Dispose();
                    _map.Dispose();
                    _request.Dispose();
                    _response.Dispose();
                    try { File.Delete(_configFile); File.Delete(_configFile + ".log"); } catch { }
                }
            });
        }
    }
}

/// <summary>
/// 把一路 48kHz 单声道音频送进 RVC：Read 时把输入攒起来，后台线程每攒够一块就转换，结果排队输出。
/// RVC 没准备好时输出静音（不让原声漏出去）；处理跟不上时丢掉积压的输入块。
/// </summary>
internal sealed class RvcStream : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _src;
    private readonly RvcHost _host;
    private readonly Queue<float> _in = new(), _out = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private float[] _buf = [];
    private bool _priming = true;
    private volatile bool _stop;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(RvcHost.Rate, 1);

    public RvcStream(ISampleProvider src48k, RvcHost host)
    {
        _src = src48k;
        _host = host;
        _thread = new Thread(Run) { IsBackground = true, Name = "rvc", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_stop) return 0;
        if (_buf.Length < count) _buf = new float[count];
        int n = _src.Read(_buf, 0, count);
        if (n == 0) return 0;
        lock (_in) for (int i = 0; i < n; i++) _in.Enqueue(_buf[i]);
        _wake.Set();
        lock (_out)
        {
            int block = Math.Max(1, _host.BlockFrames);
            if (_priming && _out.Count >= block) _priming = false; // 先攒一块，避免断断续续
            int k = 0;
            if (!_priming)
            {
                for (; k < count && _out.Count > 0; k++) buffer[offset + k] = _out.Dequeue();
                if (k < count) _priming = true;
            }
            for (; k < count; k++) buffer[offset + k] = 0;
        }
        return count;
    }

    private void Run()
    {
        float[] block = [];
        while (!_stop)
        {
            _wake.WaitOne(200);
            while (!_stop)
            {
                int size = _host.BlockFrames;
                if (size <= 0)
                {
                    lock (_in) _in.Clear(); // 还在加载：丢掉输入，输出静音
                    break;
                }
                lock (_in)
                {
                    if (_in.Count < size) break;
                    if (_in.Count > size * 3) // 积压超过 3 块：处理跟不上，丢掉旧的
                    {
                        while (_in.Count > size) _in.Dequeue();
                        _host.Dropped();
                    }
                    if (block.Length != size) block = new float[size];
                    for (int i = 0; i < size; i++) block[i] = _in.Dequeue();
                }
                var converted = _host.Process(block);
                lock (_out)
                {
                    if (converted != null) foreach (var x in converted) _out.Enqueue(x);
                    else for (int i = 0; i < size; i++) _out.Enqueue(0);
                    while (_out.Count > size * 3) _out.Dequeue();
                }
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
    }
}
