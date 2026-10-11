using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace ViaTTS.Pages;

public sealed partial class HotkeysPage : Page
{
    private static App A => App.Current;
    private readonly Debouncer _save = new();
    private bool _init = true;

    public HotkeysPage()
    {
        InitializeComponent();
        foreach (var b in new[] { HkInput, HkFocus, HkStop, HkRepeat })
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
        foreach (var b in new[] { HkInput, HkFocus, HkStop, HkRepeat })
            b.Value = hk[(string)b.Tag]?.GetValue<string>() ?? "";
        Paused.IsOn = A.Bool("hotkeys_paused");
        var o = A.Sub("overlay");
        HideAfter.IsOn = o["hide_after_send"]?.GetValue<bool>() ?? true;
        RestoreFocus.IsOn = o["restore_focus"]?.GetValue<bool>() ?? true;
        OpacitySlider.Value = Math.Round((o["opacity"] is { } op ? App.ToDouble(op) : 0.8) * 100);
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        WidthSlider.Value = Math.Round(App.ToDouble(o["width_ratio"]) * 100);
        WidthText.Text = $"{WidthSlider.Value:0}%";
        Bottom.Value = App.ToDouble(o["bottom_offset"]);
        ShowErrors();
        A.HotkeyErrorsChanged += ShowErrors;
        LangBox.SelectedItem = LangBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Lang.Current);
        _init = false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => A.HotkeyErrorsChanged -= ShowErrors;

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

    private void Bottom_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_init || double.IsNaN(args.NewValue)) return;
        A.Sub("overlay")["bottom_offset"] = (int)Math.Clamp(args.NewValue, 0, 2000);
        A.Commit("overlay");
    }

    private void Preview_Click(object sender, RoutedEventArgs e) => A.Overlay?.Show(IntPtr.Zero);
}
