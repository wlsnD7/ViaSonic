using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ViaSonic.Audio;

/// <summary>
/// 应用声音：用 Windows 的进程环回（VAD\Process_Loopback）只录选中应用（含子进程）的声音，加到虚拟麦克风的混音流里。
/// 不录整个扬声器，避免把语音软件里队友的声音再送回去。应用没开时等着，开了自动接上（3 秒检查一次）。
/// </summary>
public sealed class AppAudio
{
    private readonly BusPool _pool;
    private readonly Timer _timer;
    private readonly object _lock = new();
    // 同一时间只跑一个 Reconcile；找进程、找设备、开始录音很慢，不拿着 _lock 做（界面线程改设置、读电平时会等）
    private readonly object _work = new();
    private readonly Dictionary<int, Session> _sessions = []; // 按根进程 pid
    private readonly HashSet<int> _failed = [];
    private string? _cable;
    private Dictionary<string, float> _apps = new(StringComparer.OrdinalIgnoreCase); // exe → 音量

    /// <summary>系统不支持进程环回（激活接口返回「未实现」等）。</summary>
    public static bool Unsupported { get; private set; }
    public static event Action? UnsupportedChanged;

    public AppAudio(BusPool pool)
    {
        _pool = pool;
        _timer = new Timer(_ => Reconcile(), null, 3000, 3000);
    }

    /// <summary>cable：虚拟麦克风设备名（空＝自动），null 表示关闭；apps：exe 名 → 音量。</summary>
    public void Set(string? cable, Dictionary<string, float> apps)
    {
        lock (_lock)
        {
            bool restart = cable != _cable;
            _cable = cable;
            _apps = new Dictionary<string, float>(apps, StringComparer.OrdinalIgnoreCase);
            foreach (var (pid, s) in _sessions.ToList())
            {
                if (restart || !_apps.ContainsKey(s.Exe))
                {
                    s.Stop();
                    _sessions.Remove(pid);
                }
            }
            if (restart) _failed.Clear();
        }
        ThreadPool.QueueUserWorkItem(_ => Reconcile());
    }

    public float VolumeOf(string exe)
    {
        var apps = _apps;
        return apps.TryGetValue(exe, out var v) ? v : 0;
    }

    /// <summary>这个应用自上次读取以来送进虚拟麦克风的峰值（已乘音量），界面电平条用。</summary>
    public float TakeLevel(string exe)
    {
        float peak = 0;
        lock (_lock)
            foreach (var s in _sessions.Values)
                if (s.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)) peak = Math.Max(peak, s.TakePeak());
        return Math.Min(1f, peak * VolumeOf(exe));
    }

    private void Reconcile()
    {
        if (!Monitor.TryEnter(_work)) return; // 正在接；3 秒后的下一次会补上这期间的变化
        try
        {
            string cableSetting;
            List<string> exes;
            lock (_lock)
            {
                foreach (var (pid, s) in _sessions.ToList())
                    if (!s.Alive) _sessions.Remove(pid);
                if (_cable == null || _apps.Count == 0) return;
                cableSetting = _cable;
                exes = [.. _apps.Keys];
            }

            var roots = Procs.Roots(exes);
            List<(int Pid, string Exe)> todo;
            lock (_lock)
            {
                foreach (var (pid, s) in _sessions.ToList())
                {
                    if (roots.Any(r => r.Pid == pid)) continue;
                    s.Stop(); // 进程退出了
                    _sessions.Remove(pid);
                }
                todo = [.. roots.Where(r => !_sessions.ContainsKey(r.Pid) && !_failed.Contains(r.Pid))];
            }
            if (todo.Count == 0) return;

            using var en = new MMDeviceEnumerator();
            var name = cableSetting.Length > 0 ? cableSetting : Devices.VirtualCable();
            var cable = name.Length > 0 ? Devices.Find(en, name) : null;
            if (cable == null) return;
            foreach (var (pid, exe) in todo)
            {
                Session s;
                try
                {
                    s = new Session(pid, exe, this);
                }
                catch (Exception e)
                {
                    lock (_lock) _failed.Add(pid); // 同一个进程不反复重试，换了进程（重启应用）再试
                    Log.Error(T("无法捕获「{0}」的声音：{1}", exe, e.Message));
                    if (e is COMException { HResult: unchecked((int)0x80004001) or unchecked((int)0x80040154) } && !Unsupported)
                    {
                        Unsupported = true;
                        UnsupportedChanged?.Invoke();
                    }
                    continue;
                }
                bool keep;
                lock (_lock)
                {
                    // 接的这段时间里可能关掉了应用声音、换了虚拟麦克风或者去掉了这个应用
                    keep = _cable == cableSetting && _apps.ContainsKey(exe) && !_sessions.ContainsKey(pid);
                    if (keep) _sessions[pid] = s;
                }
                if (!keep || !_pool.Add(cable, s.Source, s.Stop))
                {
                    s.Stop();
                    if (keep) lock (_lock) _sessions.Remove(pid);
                }
            }
        }
        finally { Monitor.Exit(_work); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Set(null, []);
    }

    // ------------------------------------------------------------------ 一个进程树的录音
    private sealed class Session
    {
        private static readonly WaveFormat Format = new(48000, 16, 2);
        private readonly Ring _ring;
        private readonly Thread _thread;
        private volatile bool _alive = true;
        private int _peakBits; // float 峰值，按位存成 int 以便原子读写

        public string Exe { get; }
        public ISampleProvider Source => _ring;
        public bool Alive => _alive;

        public Session(int pid, string exe, AppAudio owner)
        {
            Exe = exe;
            _ring = new Ring(Format.SampleRate, () => owner.VolumeOf(exe));
            var client = Loopback.Activate(pid); // 失败直接抛出，由 Reconcile 记录
            var ready = new ManualResetEventSlim();
            Exception? error = null;
            _thread = new Thread(() => Run(client, ready, e => error = e)) { IsBackground = true, Name = "app-audio " + exe };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            ready.Wait();
            if (error != null) throw error;
        }

        private void Run(Loopback.IAudioClient client, ManualResetEventSlim ready, Action<Exception> fail)
        {
            using var signal = new AutoResetEvent(false);
            Loopback.IAudioCaptureClient capture;
            try
            {
                Loopback.Init(client, Format, signal);
                capture = Loopback.Capture(client);
                Marshal.ThrowExceptionForHR(client.Start());
            }
            catch (Exception e)
            {
                fail(e);
                _alive = false;
                ready.Set();
                Marshal.ReleaseComObject(client);
                return;
            }
            ready.Set();
            var buf = new byte[Format.AverageBytesPerSecond];
            try
            {
                while (_alive)
                {
                    signal.WaitOne(100);
                    while (_alive && capture.GetNextPacketSize(out var frames) == 0 && frames > 0)
                    {
                        if (capture.GetBuffer(out var data, out frames, out var flags, out _, out _) != 0) break;
                        int bytes = (int)frames * Format.BlockAlign;
                        if (buf.Length < bytes) buf = new byte[bytes];
                        if ((flags & 0x2) != 0) Array.Clear(buf, 0, bytes); // AUDCLNT_BUFFERFLAGS_SILENT
                        else Marshal.Copy(data, buf, 0, bytes);
                        capture.ReleaseBuffer(frames);
                        _ring.Write(buf, bytes, Format);
                        NotePeak(buf, bytes);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(T("无法捕获「{0}」的声音：{1}", Exe, e.Message));
            }
            finally
            {
                _alive = false;
                _ring.Close();
                try { client.Stop(); } catch { }
                Marshal.ReleaseComObject(capture);
                Marshal.ReleaseComObject(client);
            }
        }

        public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

        private void NotePeak(byte[] buf, int bytes)
        {
            int max = 0;
            for (int i = 0; i + 1 < bytes; i += 2) max = Math.Max(max, Math.Abs((int)BitConverter.ToInt16(buf, i)));
            float peak = max / 32768f;
            int cur;
            do
            {
                cur = Volatile.Read(ref _peakBits);
                if (BitConverter.Int32BitsToSingle(cur) >= peak) return;
            } while (Interlocked.CompareExchange(ref _peakBits, BitConverter.SingleToInt32Bits(peak), cur) != cur);
        }

        public void Stop()
        {
            _alive = false;
            _ring.Close();
        }
    }
}

/// <summary>进程环回的 COM 接口。NAudio 没有这部分，自己声明。</summary>
internal static class Loopback
{
    private const int ProcessLoopback = 1, IncludeTargetProcessTree = 0;
    private const uint StreamFlagsLoopback = 0x00020000, StreamFlagsEventCallback = 0x00040000, StreamFlagsAutoConvertPcm = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern IActivateAudioInterfaceAsyncOperation ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [In] ref PropVariantBlob activationParams, IActivateAudioInterfaceCompletionHandler handler);

    /// <summary>激活目标进程树的环回 IAudioClient。</summary>
    public static IAudioClient Activate(int pid)
    {
        var p = new ActivationParams { ActivationType = ProcessLoopback, TargetProcessId = (uint)pid, ProcessLoopbackMode = IncludeTargetProcessTree };
        var mem = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        try
        {
            Marshal.StructureToPtr(p, mem, false);
            var blob = new PropVariantBlob { Vt = 65 /* VT_BLOB */, Size = (uint)Marshal.SizeOf<ActivationParams>(), Data = mem };
            var handler = new Handler();
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, ref blob, handler);
            if (!handler.Done.Wait(5000)) throw new TimeoutException();
            Marshal.ThrowExceptionForHR(handler.Hr);
            return handler.Client!;
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    public static void Init(IAudioClient client, WaveFormat format, AutoResetEvent signal)
    {
        var wf = Marshal.AllocHGlobal(Marshal.SizeOf(format));
        try
        {
            Marshal.StructureToPtr(format, wf, false);
            Marshal.ThrowExceptionForHR(client.Initialize(0, StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm,
                                                          200_000 /* 20ms */, 0, wf, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(wf);
        }
        Marshal.ThrowExceptionForHR(client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle()));
    }

    public static IAudioCaptureClient Capture(IAudioClient client)
    {
        Marshal.ThrowExceptionForHR(client.GetService(typeof(IAudioCaptureClient).GUID, out var obj));
        return (IAudioCaptureClient)obj;
    }

    private sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Hr;
        public IAudioClient? Client;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out Hr, out var obj);
                if (Hr >= 0) Client = (IAudioClient)obj;
            }
            catch (Exception e)
            {
                Hr = e.HResult;
            }
            Done.Set();
        }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

/// <summary>进程快照：找应用的根进程，列出正在发声的应用。</summary>
public static class Procs
{
    public sealed record AppInfo(string Exe, string Path, string Name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int Size;
        public int Usage;
        public int Pid;
        public IntPtr HeapId;
        public int ModuleId;
        public int Threads;
        public int ParentPid;
        public int PriClassBase;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(int flags, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry e);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry e);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

    /// <summary>pid → (父 pid, exe 名)。</summary>
    public static Dictionary<int, (int Parent, string Exe)> Snapshot()
    {
        var all = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return all;
        try
        {
            var e = new ProcessEntry { Size = Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                all[e.Pid] = (e.ParentPid, e.ExeFile);
        }
        finally
        {
            CloseHandle(snap);
        }
        return all;
    }

    /// <summary>这些 exe 的根进程（父进程不是同一个 exe 的那个），浏览器等多进程应用只录一次整棵树。</summary>
    public static List<(int Pid, string Exe)> Roots(IEnumerable<string> exes)
    {
        var want = new HashSet<string>(exes, StringComparer.OrdinalIgnoreCase);
        var all = Snapshot();
        int self = Environment.ProcessId;
        return all.Where(p => p.Key != self && want.Contains(p.Value.Exe)
                              && !(all.TryGetValue(p.Value.Parent, out var parent) && parent.Exe.Equals(p.Value.Exe, StringComparison.OrdinalIgnoreCase)))
                  .Select(p => (p.Key, p.Value.Exe)).ToList();
    }

    public static bool IsRunning(string exe, Dictionary<int, (int Parent, string Exe)> snap) =>
        snap.Values.Any(p => p.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase));

    public static string PathOf(int pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>exe 的显示名：文件说明，没有就用文件名。</summary>
    public static string DisplayName(string path, string exe)
    {
        try
        {
            if (path.Length > 0 && System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) return d.Trim();
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(exe);
    }

    /// <summary>正在播放声音的应用（所有输出设备上处于活动状态的音频会话），不含 ViaSonic 自己和系统声音。</summary>
    public static List<AppInfo> Playing()
    {
        var snap = Snapshot();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AppInfo>();
        using var en = new MMDeviceEnumerator();
        foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessions = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    int pid = (int)s.GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId || s.IsSystemSoundsSession) continue;
                    if (s.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive
                        && s.AudioMeterInformation.MasterPeakValue <= 0) continue;
                    if (!snap.TryGetValue(pid, out var p) || !seen.Add(p.Exe)) continue;
                    var path = PathOf(pid);
                    list.Add(new AppInfo(p.Exe, path, DisplayName(path, p.Exe)));
                }
            }
            catch (Exception e)
            {
                Log.Warn(e.Message);
            }
        }
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
