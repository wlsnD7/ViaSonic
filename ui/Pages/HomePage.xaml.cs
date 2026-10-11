using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.System;

namespace ViaTTS.Pages;

public sealed partial class HomePage : Page
{
    private static readonly (string Key, string Name)[] Langs =
        [("zh", "中文"), ("auto", "自动识别"), ("en", "英文"), ("ja", "日文"), ("yue", "粤语"), ("ko", "韩文")];

    private static App A => App.Current;
    private readonly Debouncer _save = new();
    private bool _init = true;
    private List<(string Id, string Name)> _edge = [], _sapi = [];

    public HomePage()
    {
        InitializeComponent();
        foreach (var (_, n) in Langs)
        {
            GsvLang.Items.Add(n);
            ModelRefLang.Items.Add(n);
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _init = true;
        var engines = A.State["engines"]?.AsArray() ?? [];
        EngineBox.Items.Clear();
        foreach (var en in engines)
            EngineBox.Items.Add(new ComboBoxItem { Content = en!["label"]!.GetValue<string>(), Tag = en["key"]!.GetValue<string>() });
        EngineBox.SelectedItem = EngineBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == A.Str("engine"));

        GsvBox.Text = A.Str("gsv_home");
        GsvSpeed.Value = A.Num("gsv_speed");
        GsvSpeedText.Text = GsvSpeed.Value.ToString("0.00");
        GsvLang.SelectedIndex = LangIndex(A.Str("gsv_text_lang"));
        GsvDevice.SelectedItem = GsvDevice.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == A.Str("gsv_device"));
        EdgeRate.Value = A.Num("edge_rate");
        EdgeRateText.Text = $"{EdgeRate.Value:+0;-0;0}%";
        SapiRate.Value = A.Num("sapi_rate");
        SapiRateText.Text = $"{SapiRate.Value:+0;-0;0}";
        FillModels();
        ShowEngineOptions();
        UpdateStatus();
        A.StateChanged += UpdateStatus;
        _init = false;

        _edge = [.. Engines.EdgeTts.Voices];
        _sapi = await Task.Run(Engines.SystemTts.ListVoices);
        var cable = await Task.Run(Audio.Devices.VirtualCable);
        CableBar.IsOpen = A.Bool("mic_enabled") && cable.Length == 0;
        _init = true;
        Fill(EdgeVoice, _edge, A.Str("edge_voice"), v => v.Name);
        Fill(SapiVoice, _sapi, A.Str("sapi_voice"), v => v.Name, fallback: s => s.Contains("zh-"));
        _init = false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => A.StateChanged -= UpdateStatus;

    private static int LangIndex(string key) => Math.Max(0, Array.FindIndex(Langs, x => x.Key == key));

    private static void Fill(ComboBox box, List<(string Id, string Name)> list, string current,
                             Func<(string Id, string Name), string> label, Func<string, bool>? fallback = null)
    {
        box.Items.Clear();
        foreach (var v in list) box.Items.Add(new ComboBoxItem { Content = label(v), Tag = v.Id });
        var items = box.Items.Cast<ComboBoxItem>().ToList();
        box.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == current)
                           ?? (fallback != null ? items.FirstOrDefault(i => fallback((string)i.Content)) : null)
                           ?? items.FirstOrDefault();
    }

    // ------------------------------------------------------------------ 状态与提示
    private void UpdateStatus()
    {
        var s = A.State;
        var key = A.Str("engine");
        var en = s?["engines"]?.AsArray().FirstOrDefault(x => x!["key"]!.GetValue<string>() == key);
        bool loading = en?["loading"]?.GetValue<bool>() ?? false;
        bool ready = en?["ready"]?.GetValue<bool>() ?? false;
        var busy = s?["busy"]?.GetValue<string>() ?? "";
        StatusText.Text = string.IsNullOrEmpty(busy) ? (ready ? "就绪" : loading ? "加载中" : "未加载") : busy;
        LoadRing.IsActive = loading || busy.StartsWith("合成") || busy.StartsWith("等待") || busy.StartsWith("切换");
        LoadButton.IsEnabled = !ready && !loading;

        var kind = key == "gsv" ? en?["error_kind"]?.GetValue<string>() ?? "" : "";
        GsvBar.IsOpen = kind.Length > 0;
        if (kind == "gsv_missing")
        {
            var advice = GsvDownload.Recommend();
            GsvBar.Title = "未找到 GPT-SoVITS";
            GsvBar.Message = advice.Text + (advice.File != null ? "也可以在下方「GPT-SoVITS 目录」指定位置。" : "");
            GsvLink.Content = "下载";
            GsvLink.NavigateUri = new Uri(advice.Url);
            GsvLink.Visibility = advice.File != null ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (kind == "model_missing")
        {
            GsvBar.Title = Models.Count == 0 ? "还没有模型" : "模型文件不全";
            GsvBar.Message = en?["error"]?.GetValue<string>() ?? "";
            var url = CurrentModel()?["url"]?.GetValue<string>() ?? "";
            GsvLink.Content = "下载模型";
            GsvLink.Visibility = url.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (url.Length > 0) GsvLink.NavigateUri = new Uri(url);
            ModelPanel.IsExpanded = true;
        }
    }

    private void ShowEngineOptions()
    {
        var key = A.Str("engine");
        GsvPanel.Visibility = ModelPanel.Visibility = key == "gsv" ? Visibility.Visible : Visibility.Collapsed;
        EdgePanel.Visibility = key == "edge" ? Visibility.Visible : Visibility.Collapsed;
        SapiPanel.Visibility = key == "sapi" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init || EngineBox.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        A.Set("engine", key);
        ShowEngineOptions();
    }

    private void Load_Click(object sender, RoutedEventArgs e) => A.Speaker.LoadEngine();

    // ------------------------------------------------------------------ GPT-SoVITS
    private void GsvHome_LostFocus(object sender, RoutedEventArgs e)
    {
        var v = GsvBox.Text.Trim();
        if (v != A.Str("gsv_home")) A.Set("gsv_home", v);
    }

    private async void BrowseGsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        GsvBox.Text = folder.Path;
        A.Set("gsv_home", folder.Path);
    }

    private void GsvSpeed_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (GsvSpeedText == null) return;
        GsvSpeedText.Text = e.NewValue.ToString("0.00");
        if (!_init) _save.Run(() => A.Set("gsv_speed", Math.Round(GsvSpeed.Value, 2)));
    }

    private void GsvLang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && GsvLang.SelectedIndex >= 0) A.Set("gsv_text_lang", Langs[GsvLang.SelectedIndex].Key);
    }

    private void GsvDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && GsvDevice.SelectedItem is ComboBoxItem { Tag: string d }) A.Set("gsv_device", d);
    }

    // ------------------------------------------------------------------ 模型
    private JsonArray Models => A.Config["gsv_models"]!.AsArray();

    private JsonObject? CurrentModel()
    {
        var id = A.Str("gsv_model");
        return Models.FirstOrDefault(m => m!["id"]!.GetValue<string>() == id)?.AsObject();
    }

    private static string Field(JsonObject? m, string key) => m?[key]?.GetValue<string>() ?? "";

    private void FillModels()
    {
        bool init = _init;
        _init = true;
        ModelBox.Items.Clear();
        foreach (var m in Models)
            ModelBox.Items.Add(new ComboBoxItem { Content = Field(m!.AsObject(), "name"), Tag = Field(m.AsObject(), "id") });
        ModelBox.SelectedItem = ModelBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == A.Str("gsv_model"));
        DelModelButton.IsEnabled = Models.Count > 0;
        _init = init;
        ShowModel();
    }

    /// <summary>只刷新当前模型的字段，不动下拉列表（在 ModelBox 自己的 SelectionChanged 里清空 Items 会崩）。</summary>
    private void ShowModel()
    {
        bool init = _init;
        _init = true;
        var cur = CurrentModel();
        foreach (var c in new Control[] { ModelName, ModelGpt, ModelSovits, ModelRef, ModelRefText, ModelRefLang })
            c.IsEnabled = cur != null; // 没有模型时先点 + 添加
        ModelName.Text = Field(cur, "name");
        ModelGpt.Text = Field(cur, "gpt");
        ModelSovits.Text = Field(cur, "sovits");
        ModelRef.Text = Field(cur, "ref");
        ModelRefText.Text = Field(cur, "ref_text");
        ModelRefLang.SelectedIndex = LangIndex(Field(cur, "ref_lang"));
        _init = init;
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init || ModelBox.SelectedItem is not ComboBoxItem { Tag: string id } || id == A.Str("gsv_model")) return;
        A.Set("gsv_model", id);
        ShowModel();
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var m = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["name"] = "新模型",
            ["gpt"] = "", ["sovits"] = "", ["ref"] = "", ["ref_text"] = "", ["ref_lang"] = "zh", ["url"] = "",
        };
        Models.Add((JsonNode)m);
        A.Config["gsv_model"] = m["id"]!.GetValue<string>();
        A.Commit("gsv_models");
        A.Set("gsv_model", m["id"]!.GetValue<string>());
        FillModels();
        ModelPanel.IsExpanded = true;
        ModelName.Focus(FocusState.Programmatic);
        ModelName.SelectAll();
    }

    private async void DelModel_Click(object sender, RoutedEventArgs e)
    {
        var cur = CurrentModel();
        if (cur == null) return;
        var dlg = new ContentDialog
        {
            Title = $"删除「{Field(cur, "name")}」？",
            Content = "只移除这条配置，不会删除模型文件。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        Models.Remove(cur);
        var next = Models.Count > 0 ? Field(Models[0]!.AsObject(), "id") : "";
        A.Config["gsv_model"] = next;
        A.Commit("gsv_models");
        A.Set("gsv_model", next);
        FillModels();
    }

    private void SetModelField(string key, string value)
    {
        var cur = CurrentModel();
        if (cur == null || Field(cur, key) == value) return;
        cur[key] = value;
        A.Commit("gsv_models");
        if (key == "name") FillModels();
    }

    private void ModelField_LostFocus(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        var v = box.Text.Trim();
        if ((string)box.Tag == "name" && v.Length == 0) v = "未命名";
        SetModelField((string)box.Tag, v);
    }

    private void ModelRefLang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && ModelRefLang.SelectedIndex >= 0) SetModelField("ref_lang", Langs[ModelRefLang.SelectedIndex].Key);
    }

    private async void BrowseModelFile_Click(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag;
        var picker = new FileOpenPicker();
        string[] types = key switch
        {
            "gpt" => [".ckpt"],
            "sovits" => [".pth"],
            _ => [".wav", ".mp3", ".flac", ".ogg"],
        };
        foreach (var t in types) picker.FileTypeFilter.Add(t);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        (key switch { "gpt" => ModelGpt, "sovits" => ModelSovits, _ => ModelRef }).Text = file.Path;
        SetModelField(key, file.Path);
    }

    // ------------------------------------------------------------------ Edge / 系统语音
    private void EdgeVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && EdgeVoice.SelectedItem is ComboBoxItem { Tag: string id }) A.Set("edge_voice", id);
    }

    private void EdgeRate_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (EdgeRateText == null) return;
        EdgeRateText.Text = $"{e.NewValue:+0;-0;0}%";
        if (!_init) _save.Run(() => A.Set("edge_rate", (int)EdgeRate.Value));
    }

    private void SapiVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && SapiVoice.SelectedItem is ComboBoxItem { Tag: string id }) A.Set("sapi_voice", id);
    }

    private void SapiRate_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (SapiRateText == null) return;
        SapiRateText.Text = $"{e.NewValue:+0;-0;0}";
        if (!_init) _save.Run(() => A.Set("sapi_rate", (int)SapiRate.Value));
    }

    // ------------------------------------------------------------------ 试听
    private void TestBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && Native.IsDown(0x11))
        {
            e.Handled = true;
            Speak_Click(sender, e);
        }
    }

    private void Speak_Click(object sender, RoutedEventArgs e) => A.Speaker.Speak(TestBox.Text);
    private void Stop_Click(object sender, RoutedEventArgs e) => A.Speaker.Stop();
    private void Overlay_Click(object sender, RoutedEventArgs e) => A.Overlay?.Show(IntPtr.Zero);
}
