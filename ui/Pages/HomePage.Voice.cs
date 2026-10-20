using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ViaSonic.Asr;

namespace ViaSonic.Pages;

/// <summary>主页的语音输入卡片：识别说的话再用当前音色朗读。</summary>
public sealed partial class HomePage
{
    private void InitVoiceInput()
    {
        SttOn.IsOn = A.Bool("stt_enabled");
        Select(SttEngine, A.Str("stt_engine"));
        Select(SttMode, A.Str("stt_mode"));
        SttAzureLang.Items.Clear();
        foreach (var (id, name) in AzureAsr.Langs) SttAzureLang.Items.Add(new ComboBoxItem { Content = T(name), Tag = id });
        Select(SttAzureLang, AzureAsr.Lang(A.Config));
        SttCustomUrl.Text = A.Str("stt_custom_url");
        SttCustomModel.Text = A.Str("stt_custom_model");
        ShowVoiceInput();
    }

    private void ShowVoiceInput()
    {
        var vi = A.VoiceInput;
        var engine = A.Str("stt_engine");
        bool on = A.Bool("stt_enabled");
        bool auto = A.Str("stt_mode") == "auto";
        SttLocalPanel.Visibility = engine == "local" && !AsrModel.Ready ? Visibility.Visible : Visibility.Collapsed;
        SttAzurePanel.Visibility = engine == "azure" ? Visibility.Visible : Visibility.Collapsed;
        SttBailianPanel.Visibility = engine == "bailian" ? Visibility.Visible : Visibility.Collapsed;
        SttCustomPanel.Visibility = engine == "custom" ? Visibility.Visible : Visibility.Collapsed;
        SttModeTip.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        SttModeTitle.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;

        // 模型下载
        bool downloading = vi.Download != null;
        SttModelText.Text = downloading ? T("下载中 {0}%", (int)(vi.Download!.Value * 100))
            : vi.DownloadError.Length > 0 ? vi.DownloadError : T("未下载");
        SttModelBar.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        SttModelBar.Value = vi.Download ?? 0;
        SttDownload.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;

        // 状态：缺什么设置 > 加载 > 正在听 / 识别中 > 快捷键没设 > 就绪
        var missing = vi.Current.Missing(A.Config);
        bool loading = engine == "local" && vi.Local.Loading;
        var hk = A.Sub("hotkeys").Str("stt");
        string status = !on ? ""
            : missing.Length > 0 ? missing
            : vi.MicError.Length > 0 ? vi.MicError
            : engine == "local" && vi.Local.Error.Length > 0 ? vi.Local.Error
            : loading ? T("加载中")
            : vi.State == VoiceInput.Phase.Listening ? T("正在听")
            : vi.State == VoiceInput.Phase.Recognizing ? T("识别中")
            : !auto && hk.Length == 0 ? T("请在「快捷键」页设置「按住语音输入」。")
            : !auto && A.HotkeyErrors.TryGetValue("stt", out var err) ? A.DescribeError("stt", err)
            : T("就绪");
        SttStatus.Text = status;
        SttStatus.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SttRing.IsActive = on && (loading || vi.State == VoiceInput.Phase.Recognizing);
        SttLast.Text = vi.LastText;
        SttLastPanel.Visibility = on && vi.LastText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SttOn_Toggled(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        A.Set("stt_enabled", SttOn.IsOn); // 打开时会关掉麦克风直通，关掉时恢复
    }

    private void SttChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (_init || box.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        var key = (string)box.Tag;
        if (A.Str(key) != value) A.Set(key, value);
        ShowVoiceInput();
    }

    private void SttAzureLang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && SttAzureLang.SelectedItem is ComboBoxItem { Tag: string id }) A.Set("stt_azure_lang", id);
    }

    private async void SttDownload_Click(object sender, RoutedEventArgs e) => await A.VoiceInput.DownloadModelAsync();
}
