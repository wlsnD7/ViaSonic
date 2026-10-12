using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace ViaTTS.Pages;

public sealed partial class AudioPage : Page
{
    private static App A => App.Current;
    private readonly Debouncer _save = new();
    private bool _init = true;

    public AudioPage()
    {
        InitializeComponent();
        PttKey.HotkeyChanged += v => A.Set("ptt_key", v);
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _init = true;
        MicOn.IsOn = A.Bool("mic_enabled");
        MonOn.IsOn = A.Bool("monitor_enabled");
        PassOn.IsOn = A.Bool("mic_passthrough");
        PassVol.Value = Math.Round(A.Num("passthrough_volume") * 100);
        PassVolText.Text = $"{PassVol.Value:0}%";
        UpdatePassBar();
        MicVol.Value = Math.Round(A.Num("mic_volume") * 100);
        MonVol.Value = Math.Round(A.Num("monitor_volume") * 100);
        MicVolText.Text = $"{MicVol.Value:0}%";
        MonVolText.Text = $"{MonVol.Value:0}%";
        PlayMode.SelectedItem = PlayMode.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == A.Str("play_mode"));
        LeadSilence.Value = A.Num("lead_silence_ms");
        PttDelay.Value = A.Num("ptt_delay_ms");
        PttKey.Value = A.Str("ptt_key");
        A.ConfigReplaced += OnConfigReplaced;
        _init = false;
        await LoadDevices();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => A.ConfigReplaced -= OnConfigReplaced;

    private void OnConfigReplaced() => PttKey.Value = A.Str("ptt_key");

    private async Task LoadDevices()
    {
        try
        {
            var devices = await Task.Run(Audio.Devices.OutputNames);
            var inputs = await Task.Run(Audio.Devices.InputNames);
            var cable = devices.FirstOrDefault(n => n.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)) ?? "";
            _init = true;
            FillDevices(MicDevice, devices, A.Str("mic_device"), string.IsNullOrEmpty(cable) ? T("自动") : T("自动（{0}）", cable));
            FillDevices(MonDevice, devices, A.Str("monitor_device"), T("默认"));
            FillDevices(PassDevice, inputs, A.Str("passthrough_device"), T("默认"));
            _init = false;
            CableBar.IsOpen = string.IsNullOrEmpty(cable);
        }
        catch (Exception ex)
        {
            A.Main.ShowError(T("读取设备失败：{0}", ex.Message));
        }
    }

    private static void FillDevices(ComboBox box, List<string> devices, string current, string auto)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = auto, Tag = "" });
        foreach (var d in devices) box.Items.Add(new ComboBoxItem { Content = d, Tag = d });
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? box.Items[0];
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_init) return;
        var t = (ToggleSwitch)sender;
        A.Set((string)t.Tag, t.IsOn);
        UpdatePassBar();
    }

    private void UpdatePassBar() => PassBar.IsOpen = PassOn.IsOn && !MicOn.IsOn;

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        var box = (ComboBox)sender;
        if (box.SelectedItem is ComboBoxItem { Tag: string dev }) A.Set((string)box.Tag, dev);
    }

    private void Volume_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        var s = (Slider)sender;
        var label = s == MicVol ? MicVolText : s == MonVol ? MonVolText : PassVolText;
        if (label == null) return;
        label.Text = $"{e.NewValue:0}%";
        if (!_init) _save.Run(() => A.Set((string)s.Tag, Math.Round(s.Value / 100.0, 2)));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDevices();

    private void PlayMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_init && PlayMode.SelectedItem is ComboBoxItem { Tag: string m }) A.Set("play_mode", m);
    }

    private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_init || double.IsNaN(args.NewValue)) return;
        A.Set((string)sender.Tag, (int)Math.Clamp(args.NewValue, sender.Minimum, sender.Maximum));
    }
}
