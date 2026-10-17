using System.Text.Json.Nodes;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ViaSonic.Audio;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;

namespace ViaSonic.Pages;

/// <summary>音频页的「应用声音」：选中的应用列表，每个应用单独的音量。</summary>
public sealed partial class AudioPage
{
    private readonly DispatcherTimer _runningTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Dictionary<string, TextBlock> _notRunning = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Border Bar, double Shown)> _meters = new(StringComparer.OrdinalIgnoreCase);
    // SettingsExpander.Items 加载后再改不会刷新界面；每次换一个新列表给 ItemsSource。
    // 不能用 ObservableCollection：裁剪后的 Release 版里 WinRT 封送它会崩溃
    private const double MeterWidth = 160;

    private JsonArray Apps => A.Config["app_audio_apps"]!.AsArray();

    private static string Field(JsonNode? a, string key) => a?[key]?.GetValue<string>() ?? "";

    private void InitApps()
    {
        AppOn.IsOn = A.Bool("app_audio");
        UpdateAppBars();
        FillApps();
        AppAudio.UnsupportedChanged += OnUnsupported;
        _runningTimer.Tick -= RunningTick;
        _runningTimer.Tick += RunningTick;
        _runningTimer.Start();
        _meterTimer.Tick -= MeterTick;
        _meterTimer.Tick += MeterTick;
        _meterTimer.Start();
    }

    private void LeaveApps()
    {
        AppAudio.UnsupportedChanged -= OnUnsupported;
        _runningTimer.Stop();
        _meterTimer.Stop();
    }

    private void OnUnsupported() => A.Post(UpdateAppBars);

    private void UpdateAppBars()
    {
        AppBar.IsOpen = AppOn.IsOn && !MicOn.IsOn;
        AppUnsupportedBar.IsOpen = AppOn.IsOn && AppAudio.Unsupported;
    }

    private void FillApps()
    {
        // 每个应用一张卡片，最后是「添加应用」
        _notRunning.Clear();
        _meters.Clear();
        var cards = new List<object>();
        foreach (var a in Apps.OfType<JsonObject>()) cards.Add(AppCard(a));
        cards.Add(AddCard());
        AppPanel.ItemsSource = cards;
        RunningTick(null, null);
    }

    private SettingsCard AppCard(JsonObject a)
    {
        var exe = Field(a, "exe");
        var notRunning = new TextBlock
        {
            Text = T("未运行"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Visibility = Visibility.Collapsed,
        };
        _notRunning[exe] = notRunning;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = Field(a, "name") is { Length: > 0 } n ? n : exe });
        title.Children.Add(notRunning);
        // 电平条：送进虚拟麦克风的音量（已乘上这个应用的音量）
        var bar = new Border
        {
            Width = 0, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
        };
        var meter = new Border
        {
            Width = MeterWidth, Height = 4, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(2), Background = (Brush)Application.Current.Resources["ControlStrongFillColorDisabledBrush"],
            Child = bar,
        };
        _meters[exe] = (bar, 0);
        var header = new StackPanel();
        header.Children.Add(title);
        header.Children.Add(meter);

        var volText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, TextAlignment = TextAlignment.Right };
        var vol = new Slider { Width = 220, Minimum = 0, Maximum = 150, StepFrequency = 5, Value = Math.Round(App.ToDouble(a["volume"]) * 100) };
        volText.Text = $"{vol.Value:0}%";
        vol.ValueChanged += (_, e) =>
        {
            volText.Text = $"{e.NewValue:0}%";
            _save.Run(() =>
            {
                a["volume"] = Math.Round(vol.Value / 100.0, 2);
                A.Commit("app_audio_apps");
            });
        };
        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        ToolTipService.SetToolTip(remove, T("移除"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, T("移除"));
        remove.Click += (_, _) =>
        {
            Apps.Remove(a);
            A.Commit("app_audio_apps");
            FillApps();
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(volText);
        content.Children.Add(vol);
        content.Children.Add(remove);

        var icon = new ImageIcon { Width = 20, Height = 20 };
        _ = LoadIcon(icon, Field(a, "path"));
        return new SettingsCard { Header = header, HeaderIcon = icon, Content = content };
    }

    private static async Task LoadIcon(ImageIcon icon, string path)
    {
        if (path.Length == 0) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(thumb);
            icon.Source = bmp;
        }
        catch { } // 拿不到图标就空着
    }

    /// <summary>峰值换成 -50dB ~ 0dB 的比例，跳上去立刻显示，落下来慢慢减。</summary>
    private void MeterTick(object? sender, object? e)
    {
        foreach (var (exe, m) in _meters.ToList())
        {
            float peak = A.Speaker.Player.AppLevel(exe);
            double v = peak <= 0 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 50) / 50, 0, 1);
            double shown = Math.Max(v, m.Shown - 0.06);
            if (shown < 0.01) shown = 0;
            m.Bar.Width = shown * MeterWidth;
            _meters[exe] = (m.Bar, shown);
        }
    }

    private async void RunningTick(object? sender, object? e)
    {
        var snap = await Task.Run(Procs.Snapshot);
        foreach (var (exe, label) in _notRunning)
            label.Visibility = Procs.IsRunning(exe, snap) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ 添加
    private SettingsCard AddCard()
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        label.Children.Add(new TextBlock { Text = T("添加应用") });
        var button = new Button { Content = label };
        button.Click += AddApp_Click;
        return new SettingsCard { Content = button, HorizontalContentAlignment = HorizontalAlignment.Left, ContentAlignment = ContentAlignment.Left };
    }

    /// <summary>先取到正在发声的应用，再弹出菜单（菜单弹出后再加项不一定会显示）。</summary>
    private async void AddApp_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        List<Procs.AppInfo> playing;
        try { playing = await Task.Run(Procs.Playing); }
        finally { button.IsEnabled = true; }
        var added = new HashSet<string>(Apps.Select(a => Field(a, "exe")), StringComparer.OrdinalIgnoreCase);
        playing = playing.Where(p => !added.Contains(p.Exe)).ToList();
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var p in playing)
        {
            var icon = new ImageIcon();
            _ = LoadIcon(icon, p.Path);
            var item = new MenuFlyoutItem { Text = p.Name, Icon = icon };
            item.Click += (_, _) => AddApp(p.Exe, p.Path, p.Name);
            menu.Items.Add(item);
        }
        if (playing.Count == 0) menu.Items.Add(new MenuFlyoutItem { Text = T("没有正在发声的应用"), IsEnabled = false });
        menu.Items.Add(new MenuFlyoutSeparator());
        var browse = new MenuFlyoutItem { Text = T("浏览…"), Icon = new FontIcon { Glyph = "" } };
        browse.Click += BrowseApp_Click;
        menu.Items.Add(browse);
        menu.ShowAt(button);
    }

    private async void BrowseApp_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        AddApp(file.Name, file.Path, Procs.DisplayName(file.Path, file.Name));
    }

    private void AddApp(string exe, string path, string name)
    {
        if (exe.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)) return; // ViaSonic 自己
        if (Apps.Any(a => Field(a, "exe").Equals(exe, StringComparison.OrdinalIgnoreCase))) return;
        Apps.Add((JsonNode)new JsonObject { ["exe"] = exe, ["path"] = path, ["name"] = name, ["volume"] = 1.0 });
        A.Commit("app_audio_apps");
        FillApps();
    }
}
