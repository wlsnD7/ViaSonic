using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ViaSonic.Audio;

namespace ViaSonic;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public static readonly string Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>界面线程上的配置，只在界面线程修改。</summary>
    public JsonObject Config { get; private set; } = new();
    /// <summary>给合成、播放线程读的只读快照，每次修改配置后替换。</summary>
    private volatile JsonObject _snapshot = new();

    public Player Player { get; private set; } = null!;
    public Speaker Speaker { get; private set; } = null!;
    public Playlist Playlist { get; private set; } = null!;
    public Asr.VoiceInput VoiceInput { get; private set; } = null!;
    public AppState State { get; private set; } = AppState.Empty;
    /// <summary>GitHub 上有更新的版本时不为 null；用户关掉提示后设为 null。</summary>
    public UpdateCheck.Release? NewRelease { get; set; }
    public DispatcherQueue UI { get; private set; } = null!;
    public MainWindow Main { get; private set; } = null!;
    public OverlayWindow? Overlay { get; private set; }

    public event Action? StateChanged;
    public event Action? ConfigReplaced;
    /// <summary>用快捷键开关了按键说话（音频页刷新开关）。</summary>
    public event Action? PttToggled;
    /// <summary>语音输入的状态、识别结果、模型下载进度变了（界面线程）。</summary>
    public event Action? VoiceInputChanged;

    private TrayIcon? _tray;
    private bool _trayHintShown;

    /// <summary>托盘菜单「退出」时为 true；否则关闭主窗口只是隐藏到托盘。</summary>
    public bool Exiting { get; private set; }
    private Debouncer? _save;
    private int _errorSeq;
    private string _lastError = "";
    private IntPtr _mutex;

    public App()
    {
        // 播放线程 40ms 的缓冲经不起长时间的完整 GC（会让所有线程暂停）：尽量只做后台 GC
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log.Error(T("未处理的异常"), e.Exception);
            Main?.ShowError(e.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error(T("未处理的异常"), (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(T("未处理的异常"), e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// 界面线程上投递执行的代码（TryEnqueue、计时器、async void 的后续）里抛出的异常不会进 UnhandledException，
    /// 而是被 CoreMessaging 直接结束进程（0xC000027B）。统一在这里接住：记录堆栈、弹出提示。
    /// </summary>
    public static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Error(T("未处理的异常"), e);
            try { Current?.Main?.ShowError(e.Message); } catch { }
        }
    }

    /// <summary>投递到界面线程执行，异常由 Guard 接住。</summary>
    public bool Post(Action action) => UI.TryEnqueue(() => Guard(action));

    /// <summary>包住界面线程的 SynchronizationContext：async void 方法里的异常也走 Guard。</summary>
    private sealed class GuardedContext(SynchronizationContext inner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => inner.Post(_ => Guard(() => d(state)), null);
        public override void Send(SendOrPostCallback d, object? state) => inner.Send(_ => Guard(() => d(state)), null);
        public override SynchronizationContext CreateCopy() => this;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = Native.CreateMutex(IntPtr.Zero, false, "ViaSonic_WinUI_SingleInstance");
        if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 183)
        {
            // 已经在运行（可能在托盘里）：让它把主窗口调出来
            var other = TrayIcon.FindWindowW(TrayIcon.WindowClass, null);
            if (other != IntPtr.Zero) TrayIcon.PostMessageW(other, TrayIcon.WM_SHOW_MAIN, IntPtr.Zero, IntPtr.Zero);
            else Native.MessageBox(IntPtr.Zero, T("ViaSonic 已经在运行了。"), "ViaSonic", 0x40);
            Exit();
            return;
        }
        UI = DispatcherQueue.GetForCurrentThread();
        if (SynchronizationContext.Current is { } ctx and not GuardedContext)
            SynchronizationContext.SetSynchronizationContext(new GuardedContext(ctx));
        Config = ConfigStore.Load();
        Log.Info(T("ViaSonic 启动")); // 读完配置才知道界面语言
        _snapshot = Config.DeepClone().AsObject();
        _save = new Debouncer(300);

        Player = new Player(() => _snapshot);
        Speaker = new Speaker(() => _snapshot, Player);
        Playlist = new Playlist(this);
        VoiceInput = new Asr.VoiceInput(() => _snapshot, Speaker, Player);
        VoiceInput.Changed += () => Post(() => VoiceInputChanged?.Invoke());
        VoiceInput.OnConfig();
        Speaker.Changed += () => Post(RefreshState);
        Speaker.Failed += msg => Post(() =>
        {
            _lastError = msg;
            _errorSeq++;
            RefreshState();
        });
        RefreshState();

        Main = new MainWindow();
        Main.Activate();
        _hotkeys = new HotkeyManager(OnHotkeyThread);
        ApplyHotkeys();
        Overlay = new OverlayWindow();
        _tray = new TrayIcon();
        _tray.OpenRequested += () => Post(ShowMain);
        _tray.ExitRequested += () => Post(ExitApp);
        Lang.Changed += () =>
        {
            ApplyHotkeys();   // 重新生成快捷键错误（里面的「已被占用」等文字）
            Speaker.Gsv.RefreshError(Config); // 主页还显示着的模型错误
            RefreshState();   // 状态文字、引擎名
            // 推迟到当前事件（例如设置页下拉框的 SelectionChanged）处理完之后再重载页面
            Post(Main.ApplyLanguage);
        };
        Main.OnReady();
        Speaker.OnEngineChanged();
        _ = CheckUpdateAsync();
    }

    /// <summary>查新版本（启动时一次，「关于」页可以手动再查）。返回是否查询成功。</summary>
    public async Task<bool> CheckUpdateAsync()
    {
        var r = await UpdateCheck.CheckAsync(Version);
        NewRelease = r.Newer;
        if (NewRelease != null) Log.Info(T("发现新版本 {0}", NewRelease.Tag));
        StateChanged?.Invoke();
        return r.Ok;
    }

    private void RefreshState()
    {
        var engines = Speaker.Engines.Select(kv => new EngineState(kv.Key, kv.Value.Label, kv.Value.Ready, kv.Value.Loading,
            kv.Value.Error, kv.Value.ErrorKind, kv.Value is Engines.OnlineEngine o ? o.Missing(Config) : "")).ToList();
        State = new AppState(Speaker.Busy, _lastError, _errorSeq, engines);
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ 配置
    public string Str(string key) => Config.Str(key);
    public bool Bool(string key) => Config.Bool(key);
    public double Num(string key) => Config.Num(key);
    public JsonObject Sub(string key) => Config[key]!.AsObject();
    public JsonArray Phrases => Config["phrases"]!.AsArray();

    /// <summary>修改一个顶层配置项。</summary>
    public void Set(string key, JsonNode? value)
    {
        if (key == "ptt_key")
        {
            try { value = Ptt.Parse(value?.GetValue<string>()); }
            catch (HotkeyException e)
            {
                Main.ShowError(e.Message);
                value = "";
                Post(() => ConfigReplaced?.Invoke());
            }
        }
        Config[key] = value?.DeepClone();
        Changed(key);
    }

    /// <summary>子对象（hotkeys / overlay）或数组（phrases / gsv_models）已在本地修改。</summary>
    public void Commit(string key) => Changed(key);

    private void Changed(string key)
    {
        Asr.VoiceInput.Exclusive(Config, key); // 语音输入和麦克风直通只能开一个
        _snapshot = Config.DeepClone().AsObject();
        _save!.Run(() => ConfigStore.Save(Config));
        Player.OnConfig();
        VoiceInput?.OnConfig();
        switch (key)
        {
            case "engine":
                Speaker.OnEngineChanged();
                break;
            case "gsv_model" or "gsv_models" or "gsv_home" or "gsv_device":
                Speaker.OnModelChanged();
                break;
            case "phrases":
                ApplyHotkeys();
                Speaker.Preload();
                break;
            case "language":
                Lang.Set(Str("language"));
                break;
            case "hotkeys" or "hotkeys_paused" or "stt_enabled" or "stt_mode" or "mic_passthrough":
                ApplyHotkeys();
                break;
            case "overlay":
                Overlay?.OnConfig();
                break;
        }
        // 填了 Key 或地址后引擎变成可用，主页状态要跟着变
        if (key is "engine" or "azure_region" or "custom_url" or "xunfei_appid" || key.EndsWith("_key") || key.EndsWith("_secret")) RefreshState();
        if (key.StartsWith("stt_") || key is "azure_region" or "bailian_region" or "xunfei_appid" || key.EndsWith("_key") || key.EndsWith("_secret"))
            VoiceInputChanged?.Invoke();
    }

    public static JsonObject NewPhrase() => ConfigStore.NewPhrase();

    /// <summary>
    /// 音频条目、开了循环的文字条目：像音效一样播放，正在播放时再按一次就停止；
    /// 其他文字条目：朗读文字。
    /// </summary>
    public void PlayPhrase(JsonNode p)
    {
        var id = p.Str("id");
        var audio = p.Str("audio");
        var text = p.Str("text");
        bool loop = p.Bool("loop");
        if (audio.Length == 0 && !loop) Speaker.Speak(text, cache: true, tag: id);
        else if (Player.IsSound(id)) Player.StopSound(id);
        else if (audio.Length == 0) Speaker.SpeakAsSound(id, text);
        else Speaker.PlaySound(id, audio);
    }

    public void ShowMain()
    {
        Main.AppWindow.Show(true);
        Main.Activate();
        Native.ForceForeground(Main.Hwnd);
    }

    /// <summary>关闭主窗口：隐藏到托盘，快捷键和语音继续工作。</summary>
    public void HideToTray()
    {
        Main.AppWindow.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloon("", T("ViaSonic在后台运行"));
        }
    }

    public void ExitApp()
    {
        Exiting = true;
        Main.Close();
    }

    public void Shutdown()
    {
        ConfigStore.Save(Config);
        _hotkeys?.Dispose();
        Overlay?.Close();
        _tray?.Dispose();
        VoiceInput.Dispose();
        Speaker.Dispose();
        Player.Dispose();
        Log.Info(T("ViaSonic 退出"));
    }
}
