using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.System;

namespace ViaSonic.Pages;

public sealed partial class HomePage : Page
{
    private static readonly (string Key, string Name)[] Langs =
        [("zh", "中文"), ("auto", "自动识别"), ("en", "英文"), ("ja", "日文"), ("yue", "粤语"), ("ko", "韩文")]; // 翻译键

    private static App A => App.Current;
    private readonly Debouncer _save = new();
    private readonly Debouncer _fieldSave = new(500);
    private bool _init = true;
    private List<(string Id, string Name)> _edge = [], _sapi = [];

    public HomePage()
    {
        InitializeComponent();
        Controls.Bars.Collapse(UpdateBar, CableBar, GsvBar);
        foreach (var (_, n) in Langs)
        {
            GsvLang.Items.Add(T(n));
            ModelRefLang.Items.Add(T(n));
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
        InitOnline();
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
        Fill(EdgeVoice, _edge, A.Str("edge_voice"), v => T(v.Name));
        Fill(SapiVoice, _sapi, A.Str("sapi_voice"), v => v.Name, fallback: s => s.Contains("zh-"));
        _init = false;
        LoadVoices(A.Str("engine"));
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
        var missing = en?["missing"]?.GetValue<string>() ?? ""; // 联网服务还没填 Key 等
        StatusText.Text = string.IsNullOrEmpty(busy) ? (missing.Length > 0 ? missing : ready ? T("就绪") : loading ? T("加载中") : T("未加载")) : T(busy);
        LoadRing.IsActive = loading || busy.StartsWith("合成") || busy.StartsWith("等待") || busy.StartsWith("切换");
        var error = en?["error"]?.GetValue<string>() ?? "";
        LoadButton.Visibility = !ready && !loading && missing.Length == 0 && error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var ms = A.Speaker.LastSynthMs;
        SynthText.Text = ms > 0 ? T("上一句生成耗时 {0} ms", ms) : "";
        SynthText.Visibility = ms > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (A.NewRelease is { } rel)
        {
            UpdateBar.Title = T("发现新版本 {0}", rel.Tag);
            UpdateLink.NavigateUri = new Uri(rel.Url);
        }
        UpdateBar.IsOpen = A.NewRelease != null;

        var kind = key == "gsv" ? en?["error_kind"]?.GetValue<string>() ?? "" : "";
        GsvBar.IsOpen = kind.Length > 0;
        if (kind == "gsv_missing")
        {
            var advice = GsvDownload.Recommend();
            GsvBar.Title = T("未找到 GPT-SoVITS");
            GsvBar.Message = advice.Text + (advice.File != null ? T("也可以点 ⚙，在「GPT-SoVITS 目录」里指定位置。") : "");
            GsvLink.Content = T("下载");
            GsvLink.NavigateUri = new Uri(advice.Url);
            GsvLink.Visibility = advice.File != null ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (kind == "model_missing")
        {
            GsvBar.Title = Models.Count == 0 ? T("还没有模型") : T("模型文件不全");
            GsvBar.Message = en?["error"]?.GetValue<string>() ?? "";
            GsvLink.Visibility = Visibility.Collapsed;
            ModelPanel.IsExpanded = true;
        }
    }

    private void UpdateBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => A.NewRelease = null;

    private void ShowEngineOptions()
    {
        var key = A.Str("engine");
        GsvPanel.Visibility = ModelPanel.Visibility = GsvGear.Visibility = key == "gsv" ? Visibility.Visible : Visibility.Collapsed;
        EdgePanel.Visibility = key == "edge" ? Visibility.Visible : Visibility.Collapsed;
        SapiPanel.Visibility = key == "sapi" ? Visibility.Visible : Visibility.Collapsed;
        AzurePanel.Visibility = key == "azure" ? Visibility.Visible : Visibility.Collapsed;
        FishPanel.Visibility = key == "fish" ? Visibility.Visible : Visibility.Collapsed;
        DoubaoPanel.Visibility = key == "doubao" ? Visibility.Visible : Visibility.Collapsed;
        BailianPanel.Visibility = key == "bailian" ? Visibility.Visible : Visibility.Collapsed;
        MiniMaxPanel.Visibility = key == "minimax" ? Visibility.Visible : Visibility.Collapsed;
        CustomPanel.Visibility = key == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Engine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init || EngineBox.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        A.Set("engine", key);
        ShowEngineOptions();
        LoadVoices(key);
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
        var m = ConfigStore.NewModel();
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
            Title = T("删除「{0}」？", Field(cur, "name")),
            Content = T("只移除这条配置，不会删除模型文件。"),
            PrimaryButtonText = T("删除"),
            CloseButtonText = T("取消"),
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

    private void SetModelField(string key, string value) => SetModelField(CurrentModel(), key, value);

    private void SetModelField(JsonObject? model, string key, string value)
    {
        if (model == null || !Models.Contains(model) || Field(model, key) == value) return;
        model[key] = value;
        A.Commit("gsv_models");
        // 改名只更新下拉框里这一项；重建列表会重置正在输入的文本框
        if (key == "name" && ModelBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Field(model, "id")) is { } item)
            item.Content = value;
    }

    /// <summary>模型文件里的输入框：停止输入 0.5 秒后保存，回车或失去焦点时立即保存。</summary>
    private void SaveModelField(TextBox box, JsonObject? model, bool final)
    {
        var key = (string)box.Tag;
        var v = box.Text.Trim();
        if (key == "name" && v.Length == 0)
        {
            if (!final) return; // 正在输入时允许暂时为空
            v = T("未命名");
            _init = true;
            box.Text = v;
            _init = false;
        }
        SetModelField(model, key, v);
    }

    private void ModelField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        var box = (TextBox)sender;
        var model = CurrentModel(); // 记下当时的模型，防止切换模型后存错地方
        _fieldSave.Run(() => SaveModelField(box, model, false));
    }

    private void ModelField_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        SaveModelField((TextBox)sender, CurrentModel(), true);
    }

    private void ModelField_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveModelField((TextBox)sender, CurrentModel(), true);
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
