using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ViaTTS.Engines;

namespace ViaTTS.Pages;

/// <summary>主页里联网服务和自定义接口的设置。</summary>
public sealed partial class HomePage
{
    private readonly Debouncer _keySave = new(500);
    private List<AzureTts.AzureVoice> _azure = [];
    /// <summary>可输入的音色框：下拉项是名称，这里按顺序记着对应的 ID。</summary>
    private readonly Dictionary<ComboBox, List<(string Id, string Name)>> _editVoices = [];

    private TE Engine<TE>(string key) where TE : class => (TE)A.Speaker.Engines[key];

    private void InitOnline()
    {
        foreach (var box in new[] { AzureKey, FishKey, DoubaoKey, BailianKey, MiniMaxKey })
            box.Password = Secrets.Get(A.Config, (string)box.Tag);
        // 含花括号，写在 XAML 的标记扩展里会被当成绑定语法
        CustomUrlHeader.Tip = T("{text} 会替换成要朗读的文字。");
        CustomBodyHeader.Tip = T("{text} 会替换成要朗读的文字，接口需要直接返回音频。");
        CustomUrl.Text = A.Str("custom_url");
        CustomHeaders.Text = Secrets.Get(A.Config, "custom_headers");
        CustomBody.Text = A.Str("custom_body");
        foreach (var s in new[] { AzureRate, FishSpeed, DoubaoRate, BailianSpeed, MiniMaxSpeed })
        {
            s.Value = A.Num((string)s.Tag);
            ShowRate(s);
        }

        AzureRegion.Items.Clear();
        foreach (var r in VoicePresets.AzureRegions) AzureRegion.Items.Add(r);
        AzureRegion.SelectedItem = VoicePresets.AzureRegions.FirstOrDefault(r => r == A.Str("azure_region"));
        if (AzureRegion.SelectedItem == null) AzureRegion.Text = A.Str("azure_region");

        MiniMaxModel.Items.Clear();
        foreach (var m in VoicePresets.MiniMaxModels) MiniMaxModel.Items.Add(new ComboBoxItem { Content = m, Tag = m });
        foreach (var box in new[] { DoubaoModel, BailianRegion, BailianModel, MiniMaxRegion, MiniMaxModel, CustomMethod })
            Select(box, A.Str((string)box.Tag));

        FillDoubao();
        FillBailian();
        FillFish([]);
        FillMiniMax([]);
        FillAzure();
        ShowOnlineOptions();
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items.FirstOrDefault();

    private void ShowOnlineOptions()
    {
        BailianSpeedCard.Visibility = A.Str("bailian_model") == "cosy" ? Visibility.Visible : Visibility.Collapsed;
        CustomBodyCard.Visibility = A.Str("custom_method") == "POST" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>引擎就绪（填了 Key）时从接口取音色列表。</summary>
    private async void LoadVoices(string engine, bool refresh = false)
    {
        if (engine is not ("azure" or "fish" or "minimax") || !A.Speaker.Engines[engine].Ready) return;
        var cfg = A.Config.DeepClone().AsObject();
        try
        {
            switch (engine)
            {
                case "azure":
                    _azure = await Engine<AzureTts>("azure").VoicesAsync(cfg, refresh);
                    FillAzure();
                    break;
                case "fish":
                    FillFish(await Engine<FishAudio>("fish").MyVoicesAsync(cfg));
                    break;
                case "minimax":
                    FillMiniMax(await Engine<MiniMax>("minimax").VoicesAsync(cfg, refresh));
                    break;
            }
        }
        catch (Exception e)
        {
            A.Main.ShowError(T("读取音色列表失败：{0}", e.Message));
        }
    }

    private void RefreshVoices_Click(object sender, RoutedEventArgs e) => LoadVoices((string)((Button)sender).Tag, refresh: true);

    // ------------------------------------------------------------------ 通用：Key、选项、滑块、文本
    private void Key_Changed(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var box = (PasswordBox)sender;
        _keySave.Run(() => SaveKey(box));
    }

    private void Key_LostFocus(object sender, RoutedEventArgs e) => SaveKey((PasswordBox)sender);

    private void SaveKey(PasswordBox box)
    {
        var key = (string)box.Tag;
        var plain = box.Password.Trim();
        if (plain == Secrets.Get(A.Config, key)) return;
        A.Set(key, Secrets.Protect(plain));
        LoadVoices(key[..key.IndexOf('_')]);
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (_init || box.SelectedItem is not ComboBoxItem { Tag: string value }) return;
        var key = (string)box.Tag;
        if (A.Str(key) == value) return;
        A.Set(key, value);
        switch (key)
        {
            case "doubao_model":
                if (!Doubao.Presets(value).Any(v => v.Id == A.Str("doubao_voice"))) A.Set("doubao_voice", Doubao.Presets(value)[0].Id);
                FillDoubao();
                break;
            case "bailian_model":
                if (!Bailian.Presets(value).Any(v => v.Id == A.Str("bailian_voice"))) A.Set("bailian_voice", Bailian.Presets(value)[0].Id);
                FillBailian();
                break;
            case "minimax_region":
                LoadVoices("minimax", refresh: true);
                break;
        }
        ShowOnlineOptions();
    }

    private void Rate_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_init) return; // 构造和初始化期间（Minimum 等会触发），初始化时另外调用 ShowRate
        var s = (Slider)sender;
        ShowRate(s);
        var key = (string)s.Tag;
        _save.Run(() => A.Set(key, key.EndsWith("_rate") ? (int)s.Value : Math.Round(s.Value, 2)));
    }

    /// <summary>滑块左边的数值：*_rate 是百分比，*_speed 是倍数。</summary>
    private void ShowRate(Slider s)
    {
        var text = s == AzureRate ? AzureRateText : s == FishSpeed ? FishSpeedText : s == DoubaoRate ? DoubaoRateText
                 : s == BailianSpeed ? BailianSpeedText : MiniMaxSpeedText;
        text.Text = ((string)s.Tag).EndsWith("_rate") ? $"{s.Value:+0;-0;0}%" : s.Value.ToString("0.00");
    }

    private void Text_Changed(object sender, TextChangedEventArgs e)
    {
        if (_init) return;
        var box = (TextBox)sender;
        _fieldSave.Run(() => SaveText(box));
    }

    private void Text_LostFocus(object sender, RoutedEventArgs e) => SaveText((TextBox)sender);

    private void SaveText(TextBox box)
    {
        var key = (string)box.Tag;
        var v = box.Text.Trim();
        if (key == "custom_headers")
        {
            if (v != Secrets.Get(A.Config, key)) A.Set(key, Secrets.Protect(v));
        }
        else if (v != A.Str(key)) A.Set(key, v);
    }

    // ------------------------------------------------------------------ 可输入的音色框（Fish Audio、豆包、百炼）
    private void FillEdit(ComboBox box, List<(string Id, string Name)> list, string current)
    {
        bool init = _init;
        _init = true;
        _editVoices[box] = list;
        box.Items.Clear();
        foreach (var v in list) box.Items.Add(v.Name);
        int i = list.FindIndex(v => v.Id == current);
        box.SelectedIndex = i;
        if (i < 0) box.Text = current;
        _init = init;
    }

    private void FillDoubao() =>
        FillEdit(DoubaoVoice, Doubao.Presets(A.Str("doubao_model")).Select(v => (v.Id, v.Name)).ToList(), A.Str("doubao_voice"));

    private void FillBailian() =>
        FillEdit(BailianVoice, Bailian.Presets(A.Str("bailian_model")).Select(v => (v.Id, v.Name)).ToList(), A.Str("bailian_voice"));

    private void FillFish(List<(string Id, string Name)> mine)
    {
        var cur = A.Str("fish_voice");
        var list = mine.ToList();
        if (cur.Length > 0 && !list.Any(v => v.Id == cur)) list.Add((cur, A.Str("fish_voice_name") is { Length: > 0 } n ? n : cur));
        FillEdit(FishVoice, list, cur);
    }

    private void EditVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (_init || box.SelectedIndex < 0 || !_editVoices.TryGetValue(box, out var list) || box.SelectedIndex >= list.Count) return;
        SaveVoice(box, list[box.SelectedIndex].Id);
    }

    private void EditVoice_Submitted(ComboBox box, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        SubmitVoice(box, args.Text);
    }

    /// <summary>输入后直接点别处也保存。</summary>
    private void EditVoice_LostFocus(object sender, RoutedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (!_init && box.SelectedIndex < 0) SubmitVoice(box, box.Text);
    }

    private void SubmitVoice(ComboBox box, string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0 || !_editVoices.TryGetValue(box, out var list)) return;
        int i = list.FindIndex(v => v.Name == text || v.Id == text);
        if (i >= 0)
        {
            _init = true;
            box.SelectedIndex = i;
            _init = false;
        }
        SaveVoice(box, i >= 0 ? list[i].Id : text);
    }

    private async void SaveVoice(ComboBox box, string id)
    {
        var key = (string)box.Tag;
        if (A.Str(key) == id) return;
        A.Set(key, id);
        if (box != FishVoice) return;
        // Fish Audio 粘贴的 ID 查一下名称，下拉框里显示名称
        var list = _editVoices[box];
        var name = list.FirstOrDefault(v => v.Id == id).Name
                   ?? await Engine<FishAudio>("fish").VoiceNameAsync(A.Config.DeepClone().AsObject(), id) ?? "";
        if (A.Str("fish_voice") != id) return;
        A.Set("fish_voice_name", name);
        if (name.Length > 0 && !list.Any(v => v.Id == id)) FillFish(list);
    }

    // ------------------------------------------------------------------ MiniMax
    private void FillMiniMax(List<(string Id, string Name)> list)
    {
        bool init = _init;
        _init = true;
        var cur = A.Str("minimax_voice");
        MiniMaxVoice.Items.Clear();
        if (!list.Any(v => v.Id == cur)) MiniMaxVoice.Items.Add(new ComboBoxItem { Content = cur, Tag = cur });
        foreach (var v in list) MiniMaxVoice.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v.Id });
        Select(MiniMaxVoice, cur);
        _init = init;
    }

    // ------------------------------------------------------------------ Azure
    private void AzureRegion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && AzureRegion.SelectedItem is string r) SetAzureRegion(r);
    }

    private void AzureRegion_Submitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        SetAzureRegion(args.Text.Trim().ToLowerInvariant());
    }

    private void AzureRegion_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_init && AzureRegion.SelectedItem == null) SetAzureRegion((AzureRegion.Text ?? "").Trim().ToLowerInvariant());
    }

    private void SetAzureRegion(string region)
    {
        if (region == A.Str("azure_region")) return;
        A.Set("azure_region", region);
        LoadVoices("azure");
    }

    private void FillAzure()
    {
        bool init = _init;
        _init = true;
        AzureLang.Items.Clear();
        var lang = A.Str("azure_lang");
        foreach (var g in _azure.GroupBy(v => v.Locale).OrderBy(g => g.Key))
            AzureLang.Items.Add(new ComboBoxItem { Content = $"{g.First().LocaleName} ({g.Key})", Tag = g.Key });
        if (AzureLang.Items.Count == 0) AzureLang.Items.Add(new ComboBoxItem { Content = lang, Tag = lang });
        Select(AzureLang, lang);
        _init = init;
        FillAzureVoices();
    }

    private void FillAzureVoices()
    {
        bool init = _init;
        _init = true;
        var cur = A.Str("azure_voice");
        AzureVoice.Items.Clear();
        foreach (var v in _azure.Where(v => v.Locale == A.Str("azure_lang")))
            AzureVoice.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v.Id });
        if (AzureVoice.Items.Count == 0) AzureVoice.Items.Add(new ComboBoxItem { Content = cur, Tag = cur });
        Select(AzureVoice, cur);
        _init = init;
        FillAzureStyles();
    }

    private void FillAzureStyles()
    {
        bool init = _init;
        _init = true;
        AzureStyle.Items.Clear();
        AzureStyle.Items.Add(new ComboBoxItem { Content = T("默认"), Tag = "" });
        foreach (var s in _azure.FirstOrDefault(v => v.Id == A.Str("azure_voice"))?.Styles ?? [])
            AzureStyle.Items.Add(new ComboBoxItem { Content = s, Tag = s });
        Select(AzureStyle, A.Str("azure_style"));
        AzureStyle.IsEnabled = AzureStyle.Items.Count > 1;
        _init = init;
    }

    private void AzureLang_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init || AzureLang.SelectedItem is not ComboBoxItem { Tag: string lang }) return;
        A.Set("azure_lang", lang);
        if (_azure.FirstOrDefault(v => v.Locale == lang && v.Id == A.Str("azure_voice")) == null
            && _azure.FirstOrDefault(v => v.Locale == lang) is { } first)
        {
            A.Set("azure_voice", first.Id);
            A.Set("azure_style", "");
        }
        FillAzureVoices();
    }

    private void AzureVoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init || AzureVoice.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        A.Set("azure_voice", id);
        if (_azure.FirstOrDefault(v => v.Id == id) is { } v && !v.Styles.Contains(A.Str("azure_style"))) A.Set("azure_style", "");
        FillAzureStyles();
    }

    private void AzureStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && AzureStyle.SelectedItem is ComboBoxItem { Tag: string s }) A.Set("azure_style", s);
    }
}
