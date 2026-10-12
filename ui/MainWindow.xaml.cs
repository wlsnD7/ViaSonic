using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using ViaTTS.Pages;
using WinRT.Interop;

namespace ViaTTS;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> PageTypes = new()
    {
        ["home"] = typeof(HomePage),
        ["phrases"] = typeof(PhrasesPage),
        ["audio"] = typeof(AudioPage),
        ["hotkeys"] = typeof(HotkeysPage),
        ["about"] = typeof(AboutPage),
    };

    public IntPtr Hwnd { get; }
    private DispatcherTimer? _errorTimer;

    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ViaTTS.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        double scale = Native.GetDpiForWindow(Hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1040 * scale), (int)(760 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = (int)(720 * scale);
            p.PreferredMinimumHeight = (int)(520 * scale);
        }
        CenterOnScreen();
        AppWindow.Closing += (_, e) =>
        {
            if (App.Current.Exiting) return;
            e.Cancel = true; // 关闭按钮只是隐藏到托盘
            App.Current.HideToTray();
        };
        Closed += (_, _) =>
        {
            App.Current.Shutdown();
            App.Current.Exit();
        };
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var s = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - s.Width) / 2, area.Y + (area.Height - s.Height) / 2));
    }

    public void OnReady()
    {
        if (Content is FrameworkElement root)
        {
            // 每次启动都弹出输入框；第一次启动先显示欢迎页，关掉后再弹
            bool started = false;
            async void OnStart()
            {
                if (started) return;
                started = true;
                if (!App.Current.Bool("welcome_shown"))
                {
                    App.Current.Set("welcome_shown", true);
                    await new WelcomeDialog(root.XamlRoot).ShowAsync();
                }
                App.Current.Overlay?.Show(IntPtr.Zero);
            }
            if (root.IsLoaded) DispatcherQueue.TryEnqueue(OnStart);
            else root.Loaded += (_, _) => OnStart();
        }
        Nav.SelectedItem = Nav.MenuItems[0];
        App.Current.StateChanged += CheckErrors;
        CheckErrors();
    }

    private void CheckErrors()
    {
        var s = App.Current.State;
        int seq = (int)App.ToDouble(s?["error_seq"]);
        if (seq != _errorSeq)
        {
            if (_errorSeq >= 0) ShowError(s?["error"]?.GetValue<string>() ?? "");
            _errorSeq = seq;
        }
    }

    private int _errorSeq = -1;

    private static readonly Dictionary<string, string> NavNames = new()
    {
        ["home"] = "主页", // 翻译键
        ["phrases"] = "常用语与音效", // 翻译键
        ["audio"] = "音频", // 翻译键
        ["hotkeys"] = "设置", // 翻译键
        ["about"] = "关于", // 翻译键
    };

    /// <summary>切换语言后：导航文字换成新语言，当前页面重新创建一次。</summary>
    public void ApplyLanguage()
    {
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>())
            if (item.Tag is string tag && NavNames.TryGetValue(tag, out var zh)) item.Content = T(zh);
        if (ContentFrame.CurrentSourcePageType is { } page)
        {
            ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
            ContentFrame.BackStack.Clear();
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && PageTypes.TryGetValue(tag, out var t)
            && ContentFrame.CurrentSourcePageType != t)
            ContentFrame.Navigate(t, null, args.RecommendedNavigationTransitionInfo);
    }

    public void ShowError(string message)
    {
        ErrorBar.Title = "";
        ErrorBar.Severity = InfoBarSeverity.Error;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        _errorTimer?.Stop();
        _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _errorTimer.Tick += (_, _) => { ErrorBar.IsOpen = false; _errorTimer.Stop(); };
        _errorTimer.Start();
    }
}
