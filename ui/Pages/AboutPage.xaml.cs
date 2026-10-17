using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ViaSonic.Pages;

public sealed partial class AboutPage : Page
{
    private readonly StringBuilder _log = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _seq;

    public AboutPage()
    {
        InitializeComponent();
        ShowAbout(T("版本 {0}", App.Version));
        _timer.Tick += async (_, _) => await PollAsync();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await PollAsync();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    /// <summary>ViaSonic 卡片的说明：第一行软件介绍，第二行版本。</summary>
    private void ShowAbout(string version) =>
        AppCard.Description = T("实时文本转语音、变声器（含 RVC）、常用语和音效，以及音乐播放器。通过虚拟麦克风输出声音。") + "\n" + version;

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateRing.IsActive = true;
        UpdateLink.Visibility = Visibility.Collapsed;
        bool ok;
        try { ok = await App.Current.CheckUpdateAsync(); }
        finally
        {
            UpdateButton.IsEnabled = true;
            UpdateRing.IsActive = false;
        }
        if (App.Current.NewRelease is { } rel)
        {
            ShowAbout(T("发现新版本 {0}", rel.Tag));
            UpdateLink.NavigateUri = new Uri(rel.Url);
            UpdateLink.Visibility = Visibility.Visible;
        }
        else if (ok) ShowAbout(T("版本 {0}（已是最新版本）", App.Version));
        else App.Current.Main.ShowError(T("检查更新失败，请稍后再试"));
    }

    private Task PollAsync()
    {
        var rows = Log.Since(_seq);
        if (rows.Count == 0) return Task.CompletedTask;
        foreach (var (seq, text) in rows)
        {
            _log.AppendLine(text);
            _seq = seq;
        }
        if (_log.Length > 200_000) _log.Remove(0, _log.Length - 150_000);
        LogText.Text = _log.ToString();
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
        return Task.CompletedTask;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
    }
}
