using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ViaSonic.Audio;
using ViaSonic.Engines;
using ViaSonic.Pages;
using Windows.Storage.Pickers;

namespace ViaSonic;

/// <summary>首次启动的引导：检查虚拟麦克风、GPT-SoVITS、RVC 和语音输入模型，只出现一次。</summary>
public sealed class WelcomeDialog : ContentDialog
{
    private const string CableUrl = "https://vb-audio.com/Cable/";

    private static App A => App.Current;
    private readonly SettingsCard _gsvCard, _rvcCard, _sttCard;
    private readonly StackPanel _gsvActions, _rvcActions;

    public WelcomeDialog(XamlRoot root)
    {
        XamlRoot = root;
        Resources["ContentDialogMaxWidth"] = 640.0; // 默认 548 放不下卡片，右边会被截掉
        Title = T("欢迎使用 ViaSonic");
        PrimaryButtonText = T("开始使用");
        DefaultButton = ContentDialogButton.Primary;

        var cable = Audio.Devices.VirtualCable();
        var cableCard = new SettingsCard
        {
            Header = T("虚拟麦克风"),
            Description = cable.Length == 0 ? T("未安装，游戏里的人听不到声音")
                : cable.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase)
                    ? T("已安装（{0}）。在 Voicemeeter 里打开这一路的 B1，游戏里的麦克风选 Voicemeeter Out B1。", cable)
                    : T("已安装（{0}）", cable),
            HeaderIcon = new FontIcon { Glyph = "" },
        };
        if (cable.Length == 0)
            cableCard.Content = new HyperlinkButton { Content = T("下载 VB-Cable"), NavigateUri = new Uri(CableUrl) };

        _gsvActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _gsvCard = new SettingsCard
        {
            Header = T("GPT-SoVITS（可选）"),
            HeaderIcon = new FontIcon { Glyph = "" },
            Content = _gsvActions,
        };
        UpdateGsv(GsvEngine.FindHome(A.Str("gsv_home")));

        _rvcActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _rvcCard = new SettingsCard
        {
            Header = T("RVC（可选）"),
            HeaderIcon = new FontIcon { Glyph = "" },
            Content = _rvcActions,
        };
        UpdateRvc(RvcHost.FindHome(A.Str("rvc_home")));

        _sttCard = new SettingsCard
        {
            Header = T("语音输入模型（可选）"),
            HeaderIcon = new FontIcon { Glyph = "" },
        };
        UpdateStt();
        A.VoiceInputChanged += UpdateStt;
        Closed += (_, _) => A.VoiceInputChanged -= UpdateStt;

        var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = T("虚拟麦克风（VB-Cable 或 Voicemeeter）必须安装，GPT-SoVITS 和 RVC 按需安装。"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(cableCard);
        panel.Children.Add(_gsvCard);
        panel.Children.Add(_rvcCard);
        panel.Children.Add(_sttCard);
        Content = panel;
    }

    private void UpdateGsv(string? home, string? error = null)
    {
        _gsvActions.Children.Clear();
        if (home != null && error == null)
        {
            _gsvCard.Description = T("已找到（{0}）", home);
            return;
        }
        var advice = GsvDownload.Recommend();
        _gsvCard.Description = error ?? T("未找到。") + advice.Text;
        AddActions(_gsvActions, advice, path =>
        {
            if (GsvEngine.IsGsvHome(path))
            {
                A.Set("gsv_home", path);
                UpdateGsv(path);
            }
            else UpdateGsv(null, T("这个文件夹不是 GPT-SoVITS 整合包，请选择解压后的根目录。"));
        });
    }

    /// <summary>RVC：新版整合包算找到；旧版提示太旧；没找到时按显卡推荐下载。</summary>
    private void UpdateRvc(string? home, string? error = null)
    {
        _rvcActions.Children.Clear();
        if (error == null && RvcHost.IsHome(home))
        {
            _rvcCard.Description = T("已找到（{0}）", home!);
            return;
        }
        var advice = RvcDownload.Recommend(welcome: true);
        _rvcCard.Description = error
            ?? (RvcHost.IsOldHome(home) ? T("RVC 整合包太旧，需要 2026 年 7 月以后的版本") : T("未找到。") + advice.Text);
        AddActions(_rvcActions, advice, path =>
        {
            if (RvcHost.IsHome(path))
            {
                A.Set("rvc_home", path);
                UpdateRvc(path);
            }
            else if (RvcHost.IsOldHome(path)) UpdateRvc(path);
            else UpdateRvc(null, T("这个文件夹不是 RVC 整合包，请选择解压后的根目录。"));
        });
    }

    /// <summary>语音输入的本地识别模型：没下载时显示「下载」按钮，下载中显示进度。</summary>
    private void UpdateStt()
    {
        var vi = A.VoiceInput;
        if (Asr.AsrModel.Ready)
        {
            _sttCard.Description = T("已下载");
            _sttCard.Content = null;
            return;
        }
        if (vi.Download is { } p)
        {
            _sttCard.Description = T("下载中 {0}%", (int)(p * 100));
            _sttCard.Content = null;
            return;
        }
        _sttCard.Description = vi.DownloadError.Length > 0 ? vi.DownloadError : T("未下载");
        if (_sttCard.Content is not Button)
        {
            var download = new Button { Content = T("下载（约 230 MB）") };
            download.Click += async (_, _) => await A.VoiceInput.DownloadModelAsync();
            _sttCard.Content = download;
        }
    }

    /// <summary>「下载」（有推荐的文件时）和「选择文件夹」。</summary>
    private static void AddActions(StackPanel actions, GsvDownload.Advice advice, Action<string> picked)
    {
        if (advice.File != null)
            actions.Children.Add(new HyperlinkButton { Content = T("下载"), NavigateUri = new Uri(advice.Url) });
        var pick = new Button { Content = T("选择文件夹") };
        pick.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) picked(folder.Path);
        };
        actions.Children.Add(pick);
    }
}
