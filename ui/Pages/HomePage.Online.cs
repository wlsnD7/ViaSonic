using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ViaSonic.Engines;

namespace ViaSonic.Pages;

/// <summary>主页里联网服务和自定义接口的设置。</summary>
public sealed partial class HomePage
{
    private readonly Debouncer _keySave = new(500);
    private List<AzureTts.AzureVoice> _azure = [];
    /// <summary>Fish Audio 账号里自己创建的音色（从接口取）。</summary>
    private List<(string Id, string Name)> _fishMine = [];

    private TE Engine<TE>(string key) where TE : class => (TE)A.Speaker.Engines[key];

    private void InitOnline()
    {
        foreach (var box in new[] { AzureKey, FishKey, DoubaoKey, BailianKey, MiniMaxKey, SttAzureKey, SttBailianKey, SttCustomKey })
            box.Password = Secrets.Get(A.Config, (string)box.Tag);
        // 含花括号，写在 XAML 的标记扩展里会被当成绑定语法
        CustomUrlHeader.Tip = T("{text} 会替换成要朗读的文字。");
        CustomBodyHeader.Tip = T("{text} 会替换成要朗读的文字，接口需要直接返回音频。");
        CustomUrl.Text = A.Str("custom_url");
        CustomHeaders.Text = Secrets.Get(A.Config, "custom_headers");
        CustomBody.Text = A.Str("custom_body");
        foreach (var s in RateLabels.Keys)
        {
            s.Value = A.Num((string)s.Tag);
            ShowRate(s);
        }

        foreach (var box in new[] { AzureRegion, SttAzureRegion })
        {
            box.Items.Clear();
            foreach (var r in VoicePresets.AzureRegions) box.Items.Add(r);
        }
        ShowAzureRegion();

        MiniMaxModel.Items.Clear();
        foreach (var m in VoicePresets.MiniMaxModels) MiniMaxModel.Items.Add(new ComboBoxItem { Content = m, Tag = m });
        foreach (var box in new[] { DoubaoModel, BailianRegion, BailianModel, MiniMaxRegion, MiniMaxModel, CustomMethod, SttBailianRegion })
            Select(box, A.Str((string)box.Tag));

        FillVoices("doubao");
        FillVoices("bailian");
        FillVoices("fish");
        FillMiniMax([]);
        FillAzure();
        ShowOnlineOptions();
    }

    /// <summary>
    /// 下拉框正展开时不要重建列表（展开时清空 Items 会让 WinUI 崩溃），等收起后再刷新。
    /// 音色列表是从接口异步取回的，可能正好在用户展开下拉框时到达。
    /// </summary>
    private static bool DeferIfOpen(ComboBox box, Action refill)
    {
        if (!box.IsDropDownOpen) return false;
        void Closed(object? s, object e)
        {
            box.DropDownClosed -= Closed;
            refill();
        }
        box.DropDownClosed += Closed;
        return true;
    }

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items.FirstOrDefault();

    private void ShowOnlineOptions()
    {
        BailianSpeedCard.Visibility = A.Str("bailian_model") == "cosy" ? Visibility.Visible : Visibility.Collapsed;
        CustomBodyCard.Visibility = A.Str("custom_method") == "POST" ? Visibility.Visible : Visibility.Collapsed;
        bool clone = A.Str("doubao_model") == "icl2";
        DoubaoCloneTip.Visibility = clone ? Visibility.Visible : Visibility.Collapsed;
        DoubaoVoiceTitle.Visibility = clone ? Visibility.Collapsed : Visibility.Visible;
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
                    _fishMine = await Engine<FishAudio>("fish").MyVoicesAsync(cfg);
                    FillVoices("fish");
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
        // 语音输入卡片和引擎设置里的同一个 Key：另一个框跟着变
        foreach (var other in new[] { AzureKey, SttAzureKey, BailianKey, SttBailianKey })
            if (other != box && (string)other.Tag == key)
            {
                bool init = _init;
                _init = true;
                other.Password = plain;
                _init = init;
            }
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
            case "doubao_model" or "bailian_model":
                var engine = key[..key.IndexOf('_')];
                var list = VoiceList(engine);
                if (!list.Any(v => v.Id == A.Str(engine + "_voice"))) A.Set(engine + "_voice", list.Count > 0 ? list[0].Id : "");
                FillVoices(engine);
                break;
            case "minimax_region":
                LoadVoices("minimax", refresh: true);
                break;
            case "bailian_region":
                bool init = _init;
                _init = true;
                Select(BailianRegion, value);
                Select(SttBailianRegion, value);
                _init = init;
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

    private Dictionary<Slider, TextBlock>? _rateLabels;

    /// <summary>联网服务的语速滑块和左边显示数值的文字。</summary>
    private Dictionary<Slider, TextBlock> RateLabels => _rateLabels ??= new()
    {
        [AzureRate] = AzureRateText, [FishSpeed] = FishSpeedText, [DoubaoRate] = DoubaoRateText,
        [BailianSpeed] = BailianSpeedText, [MiniMaxSpeed] = MiniMaxSpeedText,
    };

    /// <summary>滑块左边的数值：*_rate 是百分比，*_speed 是倍数。</summary>
    private void ShowRate(Slider s)
    {
        RateLabels[s].Text = ((string)s.Tag).EndsWith("_rate") ? $"{s.Value:+0;-0;0}%" : s.Value.ToString("0.00");
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

    // ------------------------------------------------------------------ 音色列表（Fish Audio、豆包、百炼）：内置/接口取的 + 自己添加的
    private (ComboBox Box, Button Del) VoiceControls(string engine) => engine switch
    {
        "fish" => (FishVoice, FishDelVoice),
        "doubao" => (DoubaoVoice, DoubaoDelVoice),
        _ => (BailianVoice, BailianDelVoice),
    };

    /// <summary>豆包、百炼的音色分模型保存；Fish Audio 不分。</summary>
    private string VoiceModel(string engine) => engine == "fish" ? "" : A.Str(engine + "_model");

    private JsonArray SavedVoices(string engine) => A.Config[engine + "_voices"]!.AsArray();

    private List<JsonObject> SavedFor(string engine) =>
        SavedVoices(engine).OfType<JsonObject>().Where(v => v.Str("model") == VoiceModel(engine)).ToList();

    /// <summary>下拉框里的全部音色：添加的在前，然后是预置或接口取到的。</summary>
    private List<(string Id, string Name, bool Saved)> VoiceList(string engine)
    {
        var list = SavedFor(engine).Select(v => (Id: v.Str("id"), Name: v.Str("name"), Saved: true)).ToList();
        IEnumerable<(string Id, string Name)> builtIn = engine switch
        {
            "fish" => _fishMine,
            "doubao" => Doubao.Presets(A.Str("doubao_model")).Select(v => (v.Id, v.Name)),
            _ => Bailian.Presets(A.Str("bailian_model")).Select(v => (v.Id, v.Name)),
        };
        foreach (var (id, name) in builtIn)
            if (!list.Any(v => v.Id == id)) list.Add((id, name, false));
        return list.Select(v => (v.Id, v.Name.Length > 0 ? v.Name : v.Id, v.Saved)).ToList();
    }

    private void FillVoices(string engine)
    {
        var (box, del) = VoiceControls(engine);
        if (DeferIfOpen(box, () => FillVoices(engine))) return;
        bool init = _init;
        _init = true;
        var cur = A.Str(engine + "_voice");
        var list = VoiceList(engine);
        box.Items.Clear();
        foreach (var v in list) box.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v.Id });
        // 还没取到接口列表时，当前音色先按 ID 显示
        if (cur.Length > 0 && !list.Any(v => v.Id == cur)) box.Items.Insert(0, new ComboBoxItem { Content = cur, Tag = cur });
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cur);
        del.IsEnabled = list.Any(v => v.Saved && v.Id == cur);
        _init = init;
    }

    private void Voice_Changed(object sender, SelectionChangedEventArgs e)
    {
        var box = (ComboBox)sender;
        if (_init || box.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        var key = (string)box.Tag;
        var engine = key[..key.IndexOf('_')];
        if (A.Str(key) != id) A.Set(key, id);
        VoiceControls(engine).Del.IsEnabled = VoiceList(engine).Any(v => v.Saved && v.Id == id);
    }

    private async void AddVoice_Click(object sender, RoutedEventArgs e)
    {
        var engine = (string)((Button)sender).Tag;
        var input = new TextBox
        {
            Width = 360,
            PlaceholderText = engine == "fish" ? T("音色 ID 或 fish.audio 链接") : T("音色 ID"),
        };
        var dlg = new ContentDialog
        {
            Title = T("添加音色"),
            Content = input,
            PrimaryButtonText = T("添加"),
            CloseButtonText = T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var id = VoiceId(input.Text);
        if (id.Length == 0) return;

        var name = id;
        if (engine == "fish" && A.Speaker.Engines["fish"].Ready)
        {
            var found = _fishMine.FirstOrDefault(v => v.Id == id).Name
                        ?? await Engine<FishAudio>("fish").VoiceNameAsync(A.Config.DeepClone().AsObject(), id);
            if (found == null)
            {
                A.Main.ShowError(T("找不到这个音色"));
                return;
            }
            name = found;
        }
        if (!VoiceList(engine).Any(v => v.Id == id))
        {
            var v = new JsonObject { ["id"] = id, ["name"] = name };
            if (engine != "fish") v["model"] = VoiceModel(engine);
            SavedVoices(engine).Add((JsonNode)v);
            A.Commit(engine + "_voices");
        }
        A.Set(engine + "_voice", id);
        FillVoices(engine);
    }

    /// <summary>粘贴的链接取最后一段作为 ID（fish.audio/m/xxx、带查询参数的都行）。</summary>
    private static string VoiceId(string text)
    {
        text = text.Trim();
        if (!text.Contains('/')) return text;
        var path = text.Split('?', '#')[0].TrimEnd('/');
        return path[(path.LastIndexOf('/') + 1)..].Trim();
    }

    private void RemoveVoice_Click(object sender, RoutedEventArgs e)
    {
        var engine = (string)((Button)sender).Tag;
        var cur = A.Str(engine + "_voice");
        var saved = SavedFor(engine).FirstOrDefault(v => v["id"]?.GetValue<string>() == cur);
        if (saved == null) return;
        SavedVoices(engine).Remove(saved);
        A.Commit(engine + "_voices");
        var next = VoiceList(engine).FirstOrDefault().Id ?? "";
        A.Set(engine + "_voice", next);
        FillVoices(engine);
    }

    // ------------------------------------------------------------------ MiniMax
    private void FillMiniMax(List<(string Id, string Name)> list)
    {
        if (DeferIfOpen(MiniMaxVoice, () => FillMiniMax(list))) return;
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
        if (!_init && ((ComboBox)sender).SelectedItem is string r) SetAzureRegion(r);
    }

    private void AzureRegion_Submitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        args.Handled = true;
        SetAzureRegion(args.Text.Trim().ToLowerInvariant());
    }

    private void AzureRegion_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_init && sender is ComboBox { SelectedItem: null } box) SetAzureRegion((box.Text ?? "").Trim().ToLowerInvariant());
    }

    private void SetAzureRegion(string region)
    {
        if (region == A.Str("azure_region")) return;
        A.Set("azure_region", region);
        ShowAzureRegion();
        LoadVoices("azure");
    }

    /// <summary>引擎设置和语音输入卡片里的两个区域框都显示当前区域。</summary>
    private void ShowAzureRegion()
    {
        bool init = _init;
        _init = true;
        var region = A.Str("azure_region");
        foreach (var box in new[] { AzureRegion, SttAzureRegion })
        {
            box.SelectedItem = VoicePresets.AzureRegions.FirstOrDefault(r => r == region);
            if (box.SelectedItem == null) box.Text = region;
        }
        _init = init;
    }

    private void FillAzure()
    {
        if (DeferIfOpen(AzureLang, FillAzure) || DeferIfOpen(AzureVoice, FillAzure) || DeferIfOpen(AzureStyle, FillAzure)) return;
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
