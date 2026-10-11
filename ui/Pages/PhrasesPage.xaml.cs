using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using ViaTTS.Controls;

namespace ViaTTS.Pages;

public sealed partial class PhrasesPage : Page
{
    private static App A => App.Current;

    public PhrasesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Rebuild();
        ShowErrors();
        A.HotkeyErrorsChanged += ShowErrors;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => A.HotkeyErrorsChanged -= ShowErrors;

    private void ShowErrors()
    {
        var errs = A.HotkeyErrors.Where(kv => kv.Key.StartsWith("常用语")).Select(kv => $"{kv.Key}：{kv.Value}").ToList();
        ErrBar.Message = string.Join("\n", errs);
        ErrBar.IsOpen = errs.Count > 0;
    }

    private void Rebuild(string? focusId = null)
    {
        List.Children.Clear();
        var phrases = A.Phrases;
        for (int i = 0; i < phrases.Count; i++)
        {
            var card = BuildCard(phrases[i]!.AsObject(), i, phrases.Count, out var box);
            List.Children.Add(card);
            if (phrases[i]!["id"]!.GetValue<string>() == focusId)
                box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        }
        Empty.Visibility = phrases.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Button IconButton(string glyph, string tip, RoutedEventHandler click, bool enabled = true)
    {
        var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, Padding = new Thickness(10, 8, 10, 8), IsEnabled = enabled };
        ToolTipService.SetToolTip(b, tip);
        b.Click += click;
        return b;
    }

    private Border BuildCard(JsonObject p, int index, int count, out TextBox box)
    {
        var id = p["id"]!.GetValue<string>();
        var text = new TextBox
        {
            Text = p["text"]?.GetValue<string>() ?? "",
            FontSize = 15,
        };
        text.LostFocus += (_, _) =>
        {
            var t = text.Text.Trim();
            if (t == (p["text"]?.GetValue<string>() ?? "")) return;
            p["text"] = t;
            A.Commit("phrases");
        };
        box = text;

        var hk = new HotkeyButton { Value = p["hotkey"]?.GetValue<string>() ?? "" };
        hk.HotkeyChanged += v => { p["hotkey"] = v; A.Commit("phrases"); };
        ToolTipService.SetToolTip(hk, "Backspace 清除");

        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        left.Children.Add(IconButton("", "播放", (_, _) => A.PlayPhrase(p)));
        left.Children.Add(IconButton("", "上移", (_, _) => Move(index, -1), index > 0));
        left.Children.Add(IconButton("", "下移", (_, _) => Move(index, 1), index < count - 1));
        Grid.SetColumn(left, 0);
        bar.Children.Add(left);
        var mid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        mid.Children.Add(hk);
        mid.Children.Add(AudioPicker(p));
        Grid.SetColumn(mid, 1);
        bar.Children.Add(mid);

        var del = IconButton("", "删除", async (_, _) =>
        {
            var dlg = new ContentDialog
            {
                Title = "删除？",
                Content = string.IsNullOrWhiteSpace(text.Text) ? null : text.Text,
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            var arr = A.Phrases;
            var node = arr.FirstOrDefault(x => x!["id"]!.GetValue<string>() == id);
            if (node != null) arr.Remove(node);
            A.Commit("phrases");
            Rebuild();
        });
        Grid.SetColumn(del, 3);
        bar.Children.Add(del);

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(text);
        stack.Children.Add(bar);
        return new Border { Style = (Style)Application.Current.Resources["Card"], Child = stack };
    }

    private static readonly string[] AudioTypes = [".wav", ".mp3", ".flac", ".ogg", ".m4a"];

    /// <summary>自定义音频：设置后按快捷键播放该文件，代替朗读文字。</summary>
    private StackPanel AudioPicker(JsonObject p)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var label = new TextBlock { MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var pick = new Button { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 6, 14, 6) };
        var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        inner.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        inner.Children.Add(label);
        pick.Content = inner;
        var clear = IconButton("", "移除音频", (_, _) => { });

        void Refresh()
        {
            var path = p["audio"]?.GetValue<string>() ?? "";
            bool has = path.Length > 0;
            label.Text = has ? Path.GetFileName(path) + (File.Exists(path) ? "" : "（文件不存在）") : "音频";
            ToolTipService.SetToolTip(pick, has ? path : "选择音频文件，代替朗读文字");
            clear.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }

        pick.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var t in AudioTypes) picker.FileTypeFilter.Add(t);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            p["audio"] = file.Path;
            A.Commit("phrases");
            Refresh();
        };
        clear.Click += (_, _) =>
        {
            p["audio"] = "";
            A.Commit("phrases");
            Refresh();
        };
        Refresh();
        panel.Children.Add(pick);
        panel.Children.Add(clear);
        return panel;
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

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var p = App.NewPhrase();
        A.Phrases.Add((JsonNode)p);
        A.Commit("phrases");
        Rebuild(p["id"]!.GetValue<string>());
    }

}
