using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ViaTTS.Audio;

namespace ViaTTS;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public static readonly string Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>界面线程上的配置，只在界面线程修改。</summary>
    public JsonObject Config { get; private set; } = new();
    /// <summary>给合成、播放线程读的只读快照，每次修改配置后替换。</summary>
    private volatile JsonObject _snapshot = new();

    public Speaker Speaker { get; private set; } = null!;
    public JsonObject State { get; private set; } = new();
    /// <summary>GitHub 上有更新的版本时不为 null；用户关掉提示后设为 null。</summary>
    public UpdateCheck.Release? NewRelease { get; set; }
    public Dictionary<string, string> HotkeyErrors { get; private set; } = [];
    public DispatcherQueue UI { get; private set; } = null!;
    public MainWindow Main { get; private set; } = null!;
    public OverlayWindow? Overlay { get; private set; }

    public event Action? StateChanged;
    public event Action? HotkeyErrorsChanged;
    public event Action? ConfigReplaced;

    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private bool _trayHintShown;

    /// <summary>托盘菜单「退出」时为 true；否则关闭主窗口只是隐藏到托盘。</summary>
    public bool Exiting { get; private set; }
    private Debouncer? _save;
    private int _suspend, _errorSeq;
    private string _lastError = "";
    private IntPtr _mutex;

    public static readonly Dictionary<string, string> HotkeyLabels = new()
    {
        ["input"] = "打开/关闭输入框",
        ["focus"] = "聚焦输入框",
        ["stop"] = "停止",
        ["repeat"] = "重复上一句",
    };

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log.Error("未处理的异常", e.Exception);
            Main?.ShowError(e.Exception.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = Native.CreateMutex(IntPtr.Zero, false, "ViaTTS_WinUI_SingleInstance");
        if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 183)
        {
            // 已经在运行（可能在托盘里）：让它把主窗口调出来
            var other = TrayIcon.FindWindowW(TrayIcon.WindowClass, null);
            if (other != IntPtr.Zero) TrayIcon.PostMessageW(other, TrayIcon.WM_SHOW_MAIN, IntPtr.Zero, IntPtr.Zero);
            else Native.MessageBox(IntPtr.Zero, "ViaTTS 已经在运行了。", "ViaTTS", 0x40);
            Exit();
            return;
        }
        UI = DispatcherQueue.GetForCurrentThread();
        Log.Info("ViaTTS 启动");
        Config = ConfigStore.Load();
        _snapshot = Config.DeepClone().AsObject();
        _save = new Debouncer(300);

        Speaker = new Speaker(() => _snapshot);
        Speaker.Changed += () => UI.TryEnqueue(RefreshState);
        Speaker.Failed += msg => UI.TryEnqueue(() =>
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
        _tray.OpenRequested += () => UI.TryEnqueue(ShowMain);
        _tray.ExitRequested += () => UI.TryEnqueue(ExitApp);
        Main.OnReady();
        Speaker.OnEngineChanged();
        _ = CheckUpdateAsync();
    }

    private async Task CheckUpdateAsync()
    {
        NewRelease = await UpdateCheck.NewerAsync(Version);
        if (NewRelease == null) return;
        Log.Info($"发现新版本 {NewRelease.Tag}");
        StateChanged?.Invoke();
    }

    /// <summary>页面沿用的状态结构：engine / busy / error / error_seq / engines[]。</summary>
    private void RefreshState()
    {
        var engines = new JsonArray();
        foreach (var (key, e) in Speaker.Engines)
            engines.Add((JsonNode)new JsonObject
            {
                ["key"] = key, ["label"] = e.Label, ["ready"] = e.Ready, ["loading"] = e.Loading,
                ["error"] = e.Error, ["error_kind"] = e.ErrorKind,
            });
        State = new JsonObject
        {
            ["engine"] = Speaker.StatusText,
            ["busy"] = Speaker.Busy,
            ["error"] = _lastError,
            ["error_seq"] = _errorSeq,
            ["engine_key"] = Str("engine"),
            ["engines"] = engines,
        };
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------ 配置
    public string Str(string key) => Config[key]?.GetValue<string>() ?? "";
    public bool Bool(string key) => Config[key]?.GetValue<bool>() ?? false;
    public double Num(string key) => ToDouble(Config[key]);
    public JsonObject Sub(string key) => Config[key]!.AsObject();
    public JsonArray Phrases => Config["phrases"]!.AsArray();

    public static double ToDouble(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return 0;
    }

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
                UI.TryEnqueue(() => ConfigReplaced?.Invoke());
            }
        }
        Config[key] = value?.DeepClone();
        Changed(key);
    }

    /// <summary>子对象（hotkeys / overlay）或数组（phrases / gsv_models）已在本地修改。</summary>
    public void Commit(string key) => Changed(key);

    private void Changed(string key)
    {
        _snapshot = Config.DeepClone().AsObject();
        _save!.Run(() => ConfigStore.Save(Config));
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
            case "hotkeys" or "hotkeys_paused":
                ApplyHotkeys();
                break;
        }
        if (key is "engine") RefreshState();
    }

    public static JsonObject NewPhrase() => ConfigStore.NewPhrase();

    /// <summary>有自定义音频就播放音频，否则朗读文字。</summary>
    public void PlayPhrase(JsonNode p)
    {
        var audio = p["audio"]?.GetValue<string>() ?? "";
        if (audio.Length > 0) Speaker.PlayFile(audio);
        else Speaker.Speak(p["text"]?.GetValue<string>() ?? "", cache: true);
    }

    // ------------------------------------------------------------------ 快捷键
    public void SuspendHotkeys(bool on)
    {
        _suspend += on ? 1 : -1;
        ApplyHotkeys();
    }

    public void ApplyHotkeys()
    {
        if (_hotkeys == null) return;
        if (_suspend > 0)
        {
            _hotkeys.SetBindings([]);
            return;
        }
        var hk = Sub("hotkeys");
        // 「输入框」「聚焦输入框」不受「暂停快捷键」影响
        var b = new Dictionary<string, string>
        {
            ["input"] = hk["input"]?.GetValue<string>() ?? "",
            ["focus"] = hk["focus"]?.GetValue<string>() ?? "",
        };
        if (!Bool("hotkeys_paused"))
        {
            b["stop"] = hk["stop"]?.GetValue<string>() ?? "";
            b["repeat"] = hk["repeat"]?.GetValue<string>() ?? "";
            foreach (var p in Phrases)
                b["phrase:" + p!["id"]!.GetValue<string>()] = p["hotkey"]?.GetValue<string>() ?? "";
        }
        var errors = _hotkeys.SetBindings(b);
        HotkeyErrors = errors.ToDictionary(kv => DescribeBinding(kv.Key), kv => kv.Value);
        HotkeyErrorsChanged?.Invoke();
    }

    private string DescribeBinding(string name)
    {
        if (HotkeyLabels.TryGetValue(name, out var l)) return l;
        if (name.StartsWith("phrase:"))
        {
            var id = name[7..];
            var p = Phrases.FirstOrDefault(x => x!["id"]!.GetValue<string>() == id);
            var t = p?["text"]?.GetValue<string>() ?? "";
            if (t.Length == 0) t = Path.GetFileName(p?["audio"]?.GetValue<string>() ?? "") is { Length: > 0 } f ? f : "未命名";
            return $"常用语「{(t.Length > 12 ? t[..12] + "…" : t)}」";
        }
        return name;
    }

    private void OnHotkeyThread(string name)
    {
        var fg = Native.GetForegroundWindow(); // 记下游戏窗口，发送后把焦点还回去
        UI.TryEnqueue(() => OnHotkey(name, fg));
    }

    private void OnHotkey(string name, IntPtr prev)
    {
        switch (name)
        {
            case "input": Overlay?.Toggle(prev); break;
            case "focus": Overlay?.FocusIfVisible(prev); break;
            case "stop": Speaker.Stop(); break;
            case "repeat": Speaker.Repeat(); break;
            default:
                if (name.StartsWith("phrase:"))
                {
                    var id = name[7..];
                    var p = Phrases.FirstOrDefault(x => x!["id"]!.GetValue<string>() == id);
                    if (p != null) PlayPhrase(p);
                }
                break;
        }
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
            _tray?.ShowBalloon("", "ViaTTS在后台运行");
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
        Speaker.Dispose();
        Log.Info("ViaTTS 退出");
    }
}
