using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace ViaSonic.Pages;

public sealed partial class HotkeysPage : Page
{
    private static App A => App.Current;
    private readonly Debouncer _save = new();
    private bool _init = true;

    public HotkeysPage()
    {
        InitializeComponent();
        Controls.Bars.Collapse(ErrBar);
        foreach (var b in new[] { HkInput, HkFocus, HkStop, HkRepeat, HkPtt, HkPlaylist, HkNext, HkStt })
        {
            var name = (string)b.Tag;
            b.HotkeyChanged += v =>
            {
                A.Sub("hotkeys")[name] = v;
                A.Commit("hotkeys");
            };
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _init = true;
        var hk = A.Sub("hotkeys");
        foreach (var b in new[] { HkInput, HkFocus, HkStop, HkRepeat, HkPtt, HkPlaylist, HkNext, HkStt })
            b.Value = hk[(string)b.Tag]?.GetValue<string>() ?? "";
        Paused.IsOn = A.Bool("hotkeys_paused");
        var o = A.Sub("overlay");
        ShowOnStart.IsOn = o.Bool("show_on_start", true);
        SendButton.IsOn = o.Bool("send_button", true);
        MaxLines.Value = o["max_lines"] is { } ml ? Json.ToDouble(ml) : 5;
        HideAfter.IsOn = o.Bool("hide_after_send", true);
        RestoreFocus.IsOn = o.Bool("restore_focus", true);
        AwaitFocus.IsOn = o.Bool("await_focus", true);
        OpacitySlider.Value = Math.Round((o["opacity"] is { } op ? Json.ToDouble(op) : 0.75) * 100);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        WidthSlider.Value = Math.Round(o.Num("width_ratio") * 100);
        WidthText.Text = $"{WidthSlider.Value:0}%";
        LoadPosition();
        ShowErrors();
        A.HotkeyErrorsChanged += ShowErrors;
        if (A.Overlay != null) A.Overlay.Moved += OnOverlayMoved;
        LangBox.SelectedItem = LangBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Lang.Current);
        _init = false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        A.HotkeyErrorsChanged -= ShowErrors;
        if (A.Overlay != null) A.Overlay.Moved -= OnOverlayMoved;
    }

    private void LoadPosition()
    {
        var o = A.Sub("overlay");
        XSlider.Value = Math.Round((o["x_ratio"] is { } x ? Json.ToDouble(x) : 0.5) * 100);
        XText.Text = $"{XSlider.Value:0}%";
        Bottom.Value = o.Num("bottom_offset");
    }

    /// <summary>拖动输入框后刷新水平位置和底部距离。</summary>
    private void OnOverlayMoved()
    {
        bool init = _init;
        _init = true;
        LoadPosition();
        _init = init;
    }

    private void ShowErrors()
    {
        var errs = A.HotkeyErrors.Select(kv => A.DescribeError(kv.Key, kv.Value)).ToList();
        ErrBar.Message = string.Join("\n", errs);
        ErrBar.IsOpen = errs.Count > 0;
    }

    private void LangBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && LangBox.SelectedItem is ComboBoxItem { Tag: string lang }) A.Set("language", lang);
    }

    private void Paused_Toggled(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        A.Config["hotkeys_paused"] = Paused.IsOn;
        A.Commit("hotkeys_paused");
    }

    private void Overlay_Toggled(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var t = (ToggleSwitch)sender;
        A.Sub("overlay")[(string)t.Tag] = t.IsOn;
        A.Commit("overlay");
    }

    private void Opacity_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (OpacityText == null) return;
        OpacityText.Text = $"{e.NewValue:0}%";
        if (_init) return;
        _save.Run(() =>
        {
            A.Sub("overlay")["opacity"] = Math.Round(OpacitySlider.Value / 100.0, 2);
            A.Commit("overlay");
        });
    }

    private void Width_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (WidthText == null) return;
        WidthText.Text = $"{e.NewValue:0}%";
        if (_init) return;
        _save.Run(() =>
        {
            A.Sub("overlay")["width_ratio"] = Math.Round(WidthSlider.Value / 100.0, 2);
            A.Commit("overlay");
        });
    }

    private void X_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (XText == null) return;
        XText.Text = $"{e.NewValue:0}%";
        if (_init) return;
        _save.Run(() =>
        {
            A.Sub("overlay")["x_ratio"] = Math.Round(XSlider.Value / 100.0, 3);
            A.Commit("overlay");
        });
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        var o = A.Sub("overlay");
        o["x_ratio"] = 0.5;
        o["bottom_offset"] = 80;
        A.Commit("overlay");
        OnOverlayMoved();
    }

    private void Bottom_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_init || double.IsNaN(args.NewValue)) return;
        A.Sub("overlay")["bottom_offset"] = (int)Math.Clamp(args.NewValue, 0, 2000);
        A.Commit("overlay");
    }

    private void MaxLines_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_init || double.IsNaN(args.NewValue)) return;
        A.Sub("overlay")["max_lines"] = (int)Math.Clamp(Math.Round(args.NewValue), 1, 10);
        A.Commit("overlay");
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => A.Overlay?.Show(IntPtr.Zero);
}
