using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ViaTTS.Engines;
using Windows.Storage.Pickers;

namespace ViaTTS;

/// <summary>首次启动的引导：检查虚拟麦克风和 GPT-SoVITS，只出现一次。</summary>
public sealed class WelcomeDialog : ContentDialog
{
    private const string CableUrl = "https://vb-audio.com/Cable/";

    private static App A => App.Current;
    private readonly SettingsCard _gsvCard;
    private readonly StackPanel _gsvActions;

    public WelcomeDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "欢迎使用 ViaTTS";
        PrimaryButtonText = "开始使用";
        DefaultButton = ContentDialogButton.Primary;

        var cable = Audio.Devices.VirtualCable();
        var cableCard = new SettingsCard
        {
            Header = "虚拟麦克风",
            Description = cable.Length > 0 ? $"已安装（{cable}）" : "未安装，游戏里的人听不到声音",
            HeaderIcon = new FontIcon { Glyph = "" },
        };
        if (cable.Length == 0)
            cableCard.Content = new HyperlinkButton { Content = "下载 VB-Cable", NavigateUri = new Uri(CableUrl) };

        _gsvActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _gsvCard = new SettingsCard
        {
            Header = "GPT-SoVITS（可选）",
            HeaderIcon = new FontIcon { Glyph = "" },
            Content = _gsvActions,
        };
        UpdateGsv(GsvEngine.FindHome(A.Str("gsv_home")));

        var panel = new StackPanel { Spacing = 8, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = "默认使用 Edge TTS，可以直接使用。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(cableCard);
        panel.Children.Add(_gsvCard);
        Content = panel;
    }

    private void UpdateGsv(string? home, string? error = null)
    {
        _gsvActions.Children.Clear();
        if (home != null && error == null)
        {
            _gsvCard.Description = $"已找到（{home}）";
            return;
        }
        var advice = GsvDownload.Recommend();
        _gsvCard.Description = error ?? "未找到。" + advice.Text;
        if (advice.File != null)
        {
            _gsvActions.Children.Add(new HyperlinkButton { Content = "下载", NavigateUri = new Uri(advice.Url) });
            _gsvActions.Children.Add(new HyperlinkButton { Content = "7-Zip", NavigateUri = new Uri(GsvDownload.SevenZipUrl) });
        }
        var pick = new Button { Content = "选择文件夹" };
        pick.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            if (GsvEngine.IsGsvHome(folder.Path))
            {
                A.Set("gsv_home", folder.Path);
                UpdateGsv(folder.Path);
            }
            else UpdateGsv(null, "这个文件夹不是 GPT-SoVITS 整合包，请选择解压后的根目录。");
        };
        _gsvActions.Children.Add(pick);
    }
}
