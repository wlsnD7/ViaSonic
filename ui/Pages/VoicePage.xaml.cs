using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using ViaSonic.Audio;
using ViaSonic.Controls;

namespace ViaSonic.Pages;

/// <summary>变声页：开关、效果组合、效果列表。调整的是 fx_effects，点「保存」才写进当前组合。</summary>
public sealed partial class VoicePage : Page
{
    private static App A => App.Current;
    private bool _init = true;

    /// <summary>每个效果的卡片、开关、滑块区、滑块和数值。</summary>
    private readonly Dictionary<string, (Button Tile, Microsoft.UI.Xaml.Shapes.Ellipse Led, Grid Params, Knob[] Knobs, TextBlock[] Values)> _tiles = [];

    /// <summary>这个效果开着没有。</summary>
    private bool IsOn(string key) => Effects[key].Bool("on");

    public VoicePage()
    {
        InitializeComponent();
        Controls.Bars.Collapse(PassBar);
        BuildEffects();
        BuildRvc();
    }

    private JsonObject Effects => A.Config["fx_effects"]!.AsObject();
    private JsonArray Presets => A.Config["fx_presets"]!.AsArray();
    /// <summary>当前效果和组合里存的不一样（有没保存的调整）。</summary>
    private bool Dirty => Preset is { } p && p["effects"]!.ToJsonString() != Effects.ToJsonString();

    private JsonObject? Preset => Presets.OfType<JsonObject>().FirstOrDefault(p => p["id"]?.GetValue<string>() == A.Str("fx_preset"));

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _init = true;
        FxOn.IsOn = A.Bool("fx_enabled");
        FxMic.IsChecked = A.Bool("fx_mic");
        FxSpeech.IsChecked = A.Bool("fx_speech");
        ShowTab(_rvcTab);
        FillPresets();
        LoadEffects();
        LoadRvc();
        _init = false;
        UpdateState();
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var t = (ToggleSwitch)sender;
        A.Set((string)t.Tag, t.IsOn);
        EnsurePassthrough();
        UpdateState();
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var c = (CheckBox)sender;
        A.Set((string)c.Tag, c.IsChecked == true);
        EnsurePassthrough();
        UpdateState();
    }

    /// <summary>麦克风要变声时，虚拟麦克风和麦克风直通没开就一起打开。</summary>
    private static void EnsurePassthrough()
    {
        if (!A.Bool("fx_enabled") || !A.Bool("fx_mic")) return;
        if (!A.Bool("mic_enabled")) A.Set("mic_enabled", true);
        if (!A.Bool("mic_passthrough")) A.Set("mic_passthrough", true);
    }

    // ------------------------------------------------------------------ 标签
    private static bool _rvcTab; // 切换页面回来时停在上次的标签

    private void Tab_Click(object sender, RoutedEventArgs e) => ShowTab(ReferenceEquals(sender, TabRvc));

    private void ShowTab(bool rvc)
    {
        _rvcTab = rvc;
        TabEffects.IsChecked = !rvc;
        TabRvc.IsChecked = rvc;
        EffectsPanel.Visibility = rvc ? Visibility.Collapsed : Visibility.Visible;
        RvcPanel.Visibility = rvc ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>提示条、保存按钮、计数、子开关是否可用。</summary>
    private void UpdateState()
    {
        FxMic.IsEnabled = FxSpeech.IsEnabled = FxOn.IsOn;
        PassBar.IsOpen = FxOn.IsOn && FxMic.IsChecked == true && !(A.Bool("mic_enabled") && A.Bool("mic_passthrough"));
        SaveButton.IsEnabled = Dirty;
        DeleteButton.IsEnabled = Presets.Count > 1;
        int on = _tiles.Keys.Count(IsOn);
        CountText.Text = $"{on} / {VoiceFx.MaxEffects}";
        FullText.Visibility = on >= VoiceFx.MaxEffects ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (key, t) in _tiles) t.Tile.IsEnabled = IsOn(key) || on < VoiceFx.MaxEffects;
    }

    // ------------------------------------------------------------------ 效果卡片
    /// <summary>每个效果一张卡片：名称 + 右上角开关，下面是参数滑块。开着的卡片用主题色描边。</summary>
    private void BuildEffects()
    {
        var res = Application.Current.Resources;
        var items = new List<object>();
        foreach (var def in VoiceFx.Defs)
        {
            // 标题行：效果名 + 右上角指示灯
            var led = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 10, Height = 10, StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            var title = new Grid();
            title.Children.Add(new TextBlock { Text = T(def.Name), Style = (Style)res["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(led);
            // 参数：旋钮并排，下面是数值和名称
            var panel = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            var knobs = new Knob[def.Params.Length];
            var values = new TextBlock[def.Params.Length];
            for (int i = 0; i < def.Params.Length; i++)
            {
                var p = def.Params[i];
                panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
                knobs[i] = new Knob { Minimum = p.Min, Maximum = p.Max, Step = p.Step, Tag = (def, i), HorizontalAlignment = HorizontalAlignment.Center };
                SetName(knobs[i], T(p.Name));
                knobs[i].ValueChanged += Param_Changed;
                values[i] = new TextBlock { Style = (Style)res["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center };
                var col = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        knobs[i], values[i],
                        new TextBlock
                        {
                            Text = T(p.Name), Style = (Style)res["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)res["TextFillColorSecondaryBrush"],
                        },
                    },
                };
                Grid.SetColumn(col, i);
                panel.Children.Add(col);
            }

            var tile = new Button
            {
                Content = new StackPanel { Spacing = 6, Children = { title, panel } },
                Tag = def.Key,
                Padding = new Thickness(14, 10, 12, 12),
                CornerRadius = (CornerRadius)res["OverlayCornerRadius"],
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
            };
            // 开满 6 个时其他卡片不能点，但保持卡片的样子（不要按钮禁用时的灰底）
            tile.Resources["ButtonBackgroundDisabled"] = res["CardBackgroundFillColorDefaultBrush"];
            tile.Resources["ButtonBorderBrushDisabled"] = res["CardStrokeColorDefaultBrush"];
            SetName(tile, T(def.Name));
            tile.Click += Effect_Toggled;
            items.Add(tile);
            _tiles[def.Key] = (tile, led, panel, knobs, values);
        }
        EffectGrid.ItemsSource = items; // 不能用 ObservableCollection（裁剪后的 Release 会崩）
    }

    private static void SetName(UIElement el, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(el, name);

    /// <summary>开着：主题色描边、淡淡的主题色底，旋钮可调；关着：旋钮变淡、不能调。</summary>
    private void ShowOn(string key, bool on)
    {
        var t = _tiles[key];
        var res = Application.Current.Resources;
        t.Tile.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)res[on ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];
        t.Tile.BorderThickness = new Thickness(on ? 1.5 : 1);
        var accent = (Windows.UI.Color)res["SystemAccentColor"];
        t.Tile.Background = on
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x24, accent.R, accent.G, accent.B))
            : (Microsoft.UI.Xaml.Media.Brush)res["CardBackgroundFillColorDefaultBrush"];
        // 指示灯：开着是实心的绿灯，关着是空心圆圈
        t.Led.Fill = on ? (Microsoft.UI.Xaml.Media.Brush)res["SystemFillColorSuccessBrush"] : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        t.Led.Stroke = (Microsoft.UI.Xaml.Media.Brush)res[on ? "SystemFillColorSuccessBrush" : "ControlStrongStrokeColorDefaultBrush"];
        t.Params.Opacity = on ? 1 : 0.45;
        foreach (var k in t.Knobs) k.IsEnabled = on;
    }

    /// <summary>把 fx_effects 显示到卡片上。</summary>
    private void LoadEffects()
    {
        bool was = _init;
        _init = true;
        foreach (var def in VoiceFx.Defs)
        {
            var t = _tiles[def.Key];
            var e = Effects[def.Key] as JsonObject;
            ShowOn(def.Key, IsOn(def.Key));
            for (int i = 0; i < def.Params.Length; i++)
            {
                var p = def.Params[i];
                t.Knobs[i].Value = Math.Clamp(e?[p.Key] is { } n ? Json.ToDouble(n) : p.Default, p.Min, p.Max);
                t.Values[i].Text = Format(p, t.Knobs[i].Value);
            }
        }
        _init = was;
    }

    private static string Format(FxParam p, double v) => p.Unit switch
    {
        "st" or "dB" => (v > 0 ? "+" : "") + v.ToString("0") + (p.Unit == "dB" ? " dB" : ""),
        "%" => $"{v:0}%",
        "Hz" => p.Step < 1 ? $"{v:0.0} Hz" : $"{v:0} Hz",
        "ms" => $"{v:0} ms",
        "ratio" => $"{v:0.#}:1",
        _ => $"{v:0}",
    };

    private void Effect_Toggled(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var key = (string)((Button)sender).Tag;
        bool on = !IsOn(key);
        Effects[key]!["on"] = on;
        ShowOn(key, on);
        A.Commit("fx_effects");
        UpdateState();
    }

    private void Param_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        var s = (Knob)sender;
        var (def, i) = ((FxDef, int))s.Tag;
        _tiles[def.Key].Values[i].Text = Format(def.Params[i], s.Value);
        if (_init) return;
        Effects[def.Key]![def.Params[i].Key] = s.Value;
        A.Commit("fx_effects");
        UpdateState();
    }

    // ------------------------------------------------------------------ 组合
    private void FillPresets()
    {
        bool was = _init;
        _init = true;
        var items = Presets.OfType<JsonObject>()
            .Select(p => new ComboBoxItem { Content = p.Str("name"), Tag = p.Str("id") })
            .ToList();
        PresetBox.ItemsSource = items;
        PresetBox.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == A.Str("fx_preset"));
        _init = was;
    }

    private async void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init || PresetBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        if (Dirty && Preset is { } old)
        {
            var dlg = new ContentDialog
            {
                Title = T("组合「{0}」有没保存的调整", old.Str("name")),
                PrimaryButtonText = T("保存"),
                SecondaryButtonText = T("不保存"),
                CloseButtonText = T("取消"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            var choice = await dlg.ShowAsync();
            if (choice == ContentDialogResult.None)
            {
                FillPresets(); // 取消：选回原来的组合
                return;
            }
            if (choice == ContentDialogResult.Primary)
            {
                old["effects"] = Effects.DeepClone();
                A.Commit("fx_presets");
            }
        }
        A.Config["fx_preset"] = id;
        A.Set("fx_effects", Preset!["effects"]); // 没保存的调整丢掉，换成这个组合的效果
        LoadEffects();
        LoadRvc();
        UpdateState();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Preset is not { } p) return;
        p["effects"] = Effects.DeepClone();
        A.Commit("fx_presets");
        UpdateState();
    }

    private async Task<string?> AskName(string title, string primary, string text)
    {
        var box = new TextBox { Width = 320, Text = text, PlaceholderText = T("新组合") };
        box.SelectAll();
        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        var name = box.Text.Trim();
        return name.Length > 0 ? name : T("新组合");
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (await AskName(T("添加组合"), T("添加"), "") is not { } name) return;
        var p = ConfigStore.NewPreset(name, Effects);
        Presets.Add((JsonNode)p);
        A.Config["fx_preset"] = p.Str("id");
        A.Commit("fx_presets");
        FillPresets();
        UpdateState();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Preset is not { } p) return;
        if (await AskName(T("重命名组合"), T("保存"), p.Str("name")) is not { } name) return;
        p["name"] = name;
        A.Commit("fx_presets");
        FillPresets();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Preset is not { } p || Presets.Count <= 1) return;
        var dlg = new ContentDialog
        {
            Title = T("删除组合「{0}」？", p.Str("name")),
            PrimaryButtonText = T("删除"),
            CloseButtonText = T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        Presets.Remove(p);
        var next = Presets.OfType<JsonObject>().First();
        A.Config["fx_preset"] = next.Str("id");
        A.Commit("fx_presets");
        A.Set("fx_effects", next["effects"]);
        FillPresets();
        LoadEffects();
        LoadRvc();
        UpdateState();
    }
}
