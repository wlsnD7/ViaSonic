using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using ViaTTS.Controls;

namespace ViaTTS.Pages;

public sealed partial class PhrasesPage : Page
{
    private static App A => App.Current;
    private readonly Debouncer _save = new();
    private readonly Dictionary<string, (Border Row, Button Play)> _rows = [];
    private bool _init = true;

    public PhrasesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _init = true;
        SoundVol.Value = Math.Round(A.Num("sound_volume") * 100);
        SoundVolText.Text = $"{SoundVol.Value:0}%";
        _init = false;
        Rebuild();
        ShowErrors();
        A.HotkeyErrorsChanged += ShowErrors;
        A.Speaker.Player.ActiveChanged += OnActiveChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        A.HotkeyErrorsChanged -= ShowErrors;
        A.Speaker.Player.ActiveChanged -= OnActiveChanged;
    }

    private void ShowErrors()
    {
        var errs = A.HotkeyErrors.Where(kv => kv.Key.StartsWith("phrase:")).Select(kv => A.DescribeError(kv.Key, kv.Value)).ToList();
        ErrBar.Message = string.Join("\n", errs);
        ErrBar.IsOpen = errs.Count > 0;
    }

    private void OnActiveChanged() => A.UI.TryEnqueue(UpdateActive);

    /// <summary>正在播放的那一条高亮，播放按钮变成停止。</summary>
    private void UpdateActive()
    {
        foreach (var (id, (row, play)) in _rows)
        {
            bool on = A.Speaker.Player.IsActive(id);
            row.BorderBrush = (Brush)Application.Current.Resources[on ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];
            ((FontIcon)play.Content).Glyph = on ? "" : "";
            ToolTipService.SetToolTip(play, on ? T("停止") : T("播放"));
            AutomationProperties.SetName(play, on ? T("停止") : T("播放"));
        }
    }

    private void Rebuild(string? focusId = null)
    {
        List.Children.Clear();
        _rows.Clear();
        var phrases = A.Phrases;
        for (int i = 0; i < phrases.Count; i++)
        {
            var row = BuildRow(phrases[i]!.AsObject(), i, phrases.Count, out var box);
            List.Children.Add(row);
            if (phrases[i]!["id"]!.GetValue<string>() == focusId)
                box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        }
        Empty.Visibility = phrases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActive();
    }

    private static Button IconButton(string glyph, string tip, RoutedEventHandler? click = null)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8) };
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, tip);
        if (click != null) b.Click += click;
        return b;
    }

    private static T At<T>(T el, int col) where T : FrameworkElement
    {
        Grid.SetColumn(el, col);
        el.VerticalAlignment = VerticalAlignment.Center;
        return el;
    }

    private Border BuildRow(JsonObject p, int index, int count, out TextBox box)
    {
        var id = p["id"]!.GetValue<string>();
        var audio = p["audio"]?.GetValue<string>() ?? "";
        bool isAudio = audio.Length > 0;

        var grid = new Grid { ColumnSpacing = 8 };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(40), new GridLength(170), GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        var play = IconButton("", T("播放"), (_, _) =>
        {
            if (!isAudio && A.Speaker.Player.IsActive(id)) A.Speaker.StopSpeech();
            else A.PlayPhrase(p);
        });
        grid.Children.Add(At(play, 0));
        grid.Children.Add(At(new FontIcon
        {
            Glyph = isAudio ? "" : "", FontSize = 14,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        }, 1));

        // 文字条目编辑朗读的文字；音频条目编辑名称，留空时显示文件名
        var key = isAudio ? "name" : "text";
        var text = new TextBox { Text = p[key]?.GetValue<string>() ?? "" };
        if (isAudio)
        {
            text.PlaceholderText = Path.GetFileName(audio) + (File.Exists(audio) ? "" : T("（文件不存在）"));
            ToolTipService.SetToolTip(text, audio);
        }
        text.LostFocus += (_, _) =>
        {
            var t = text.Text.Trim();
            if (t == (p[key]?.GetValue<string>() ?? "")) return;
            p[key] = t;
            A.Commit("phrases");
        };
        box = text;
        grid.Children.Add(At(text, 2));

        var hk = new HotkeyButton { Value = p["hotkey"]?.GetValue<string>() ?? "" };
        hk.HotkeyChanged += v => { p["hotkey"] = v; A.Commit("phrases"); };
        ToolTipService.SetToolTip(hk, T("Backspace 清除"));
        grid.Children.Add(At(hk, 3));

        if (isAudio)
        {
            var loop = new ToggleButton
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8),
                IsChecked = p["loop"]?.GetValue<bool>() ?? false,
            };
            ToolTipService.SetToolTip(loop, T("循环"));
            AutomationProperties.SetName(loop, T("循环"));
            loop.Click += (_, _) => { p["loop"] = loop.IsChecked == true; A.Commit("phrase_props"); };
            grid.Children.Add(At(loop, 4));

            var volText = new TextBlock { MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
            var vol = new Slider
            {
                Width = 120, Minimum = 0, Maximum = 150, StepFrequency = 5, VerticalAlignment = VerticalAlignment.Center,
                Value = Math.Round(App.ToDouble(p["volume"]) * 100),
            };
            volText.Text = $"{vol.Value:0}%";
            ToolTipService.SetToolTip(vol, T("音量"));
            AutomationProperties.SetName(vol, T("音量"));
            vol.ValueChanged += (_, e) =>
            {
                volText.Text = $"{e.NewValue:0}%";
                p["volume"] = Math.Round(e.NewValue / 100.0, 2);
                A.Commit("phrase_props");
            };
            var volPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            volPanel.Children.Add(vol);
            volPanel.Children.Add(volText);
            grid.Children.Add(At(volPanel, 5));
        }

        grid.Children.Add(At(MoreButton(p, id, index, count, isAudio, text), 6));

        var row = new Border { Style = (Style)Application.Current.Resources["Card"], Padding = new Thickness(8, 6, 8, 6), Child = grid };
        _rows[id] = (row, play);
        return row;
    }

    private Button MoreButton(JsonObject p, string id, int index, int count, bool isAudio, TextBox text)
    {
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string label, string glyph, RoutedEventHandler click, bool enabled = true)
        {
            var it = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            it.Click += click;
            menu.Items.Add(it);
            return it;
        }
        Item(T("上移"), "", (_, _) => Move(index, -1), index > 0);
        Item(T("下移"), "", (_, _) => Move(index, 1), index < count - 1);
        if (isAudio)
            Item(T("更换文件"), "", async (_, _) =>
            {
                var file = (await PickAudio(false)).FirstOrDefault();
                if (file == null) return;
                p["audio"] = file;
                A.Commit("phrases");
                Rebuild();
            });
        menu.Items.Add(new MenuFlyoutSeparator());
        Item(T("删除"), "", async (_, _) =>
        {
            var shown = text.Text.Length > 0 ? text.Text : text.PlaceholderText;
            var dlg = new ContentDialog
            {
                Title = T("删除？"),
                Content = string.IsNullOrWhiteSpace(shown) ? null : shown,
                PrimaryButtonText = T("删除"),
                CloseButtonText = T("取消"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            A.Speaker.Player.StopSound(id);
            var arr = A.Phrases;
            var node = arr.FirstOrDefault(x => x!["id"]!.GetValue<string>() == id);
            if (node != null) arr.Remove(node);
            A.Commit("phrases");
            Rebuild();
        });
        var more = IconButton("", T("更多"));
        more.Flyout = menu;
        return more;
    }

    private static readonly string[] AudioTypes = [".wav", ".mp3", ".flac", ".ogg", ".m4a"];

    private static async Task<List<string>> PickAudio(bool multiple)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var t in AudioTypes) picker.FileTypeFilter.Add(t);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        if (!multiple) return await picker.PickSingleFileAsync() is { } f ? [f.Path] : [];
        return (await picker.PickMultipleFilesAsync()).Select(f => f.Path).ToList();
    }

    private void AddAudio(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var path in paths)
        {
            var p = App.NewPhrase();
            p["audio"] = path;
            A.Phrases.Add((JsonNode)p);
            n++;
        }
        if (n == 0) return;
        A.Commit("phrases");
        Rebuild();
    }

    private void Move(int index, int step)
    {
        var arr = A.Phrases;
        int j = index + step;
        if (j < 0 || j >= arr.Count) return;
        var a = arr[index]!.DeepClone();
        var b = arr[j]!.DeepClone();
        arr[index] = b;
        arr[j] = a;
        A.Commit("phrases");
        Rebuild();
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var p = App.NewPhrase();
        A.Phrases.Add((JsonNode)p);
        A.Commit("phrases");
        Rebuild(p["id"]!.GetValue<string>());
    }

    private async void AddAudio_Click(object sender, RoutedEventArgs e) => AddAudio(await PickAudio(true));

    private void StopAll_Click(object sender, RoutedEventArgs e) => A.Speaker.Stop();

    private void SoundVol_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (SoundVolText == null) return;
        SoundVolText.Text = $"{e.NewValue:0}%";
        if (!_init) _save.Run(() => A.Set("sound_volume", Math.Round(SoundVol.Value / 100.0, 2)));
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var ok = new List<string>();
        var bad = new List<string>();
        foreach (var it in items)
        {
            if (it is StorageFile f && AudioTypes.Contains(Path.GetExtension(f.Path).ToLowerInvariant())) ok.Add(f.Path);
            else bad.Add(it.Name);
        }
        AddAudio(ok);
        if (bad.Count > 0) A.Main.ShowError(T("不支持的文件：{0}", string.Join(", ", bad)));
    }
}
