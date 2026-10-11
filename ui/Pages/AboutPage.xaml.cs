using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ViaTTS.Pages;

public sealed partial class AboutPage : Page
{
    private readonly StringBuilder _log = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _seq;

    public AboutPage()
    {
        InitializeComponent();
        AppCard.Description = $"版本 {App.Version}";
        _timer.Tick += async (_, _) => await PollAsync();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        await PollAsync();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

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
