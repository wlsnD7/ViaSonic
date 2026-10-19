using System.Text;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using ViaSonic.Audio;

namespace ViaSonic.Pages;

/// <summary>常用语页的「播放列表」标签：封面、播放控制、倍速、播放模式、进度、曲目列表（拖动排序、双击播放）、导入导出歌单。</summary>
public sealed partial class PhrasesPage
{
    private static bool _playlistTab; // 切换页面回来时停在上次的标签
    private bool _seeking;            // 代码在更新进度条，不算拖动
    private bool _dragSeek;           // 正按着进度条，定时器不去改它
    private readonly Dictionary<string, TextBlock> _durations = []; // 路径 → 时长文字（读文件头比较慢，异步填）

    private static Playlist Pl => A.Playlist;

    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 2];

    /// <summary>封面，按路径缓存；null＝没有嵌入封面。</summary>
    private static readonly Dictionary<string, BitmapImage?> Covers = [];
    private string? _coverPath; // 封面区现在显示的是哪首

    private void InitPlaylist()
    {
        ShowMode();
        ShowSpeed();
        ShowFade();
        _coverPath = null;
        ShowTab(_playlistTab);
        FillPlaylist();
        UpdatePlayer();
        Pl.Changed += OnPlaylistChanged;
        PlSeek.AddHandler(PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _dragSeek = true), true);
        PlSeek.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _dragSeek = false), true);
        PlSeek.AddHandler(PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _dragSeek = false), true);
    }

    private void LeavePlaylist() => Pl.Changed -= OnPlaylistChanged;

    private void OnPlaylistChanged() => A.Post(() =>
    {
        UpdatePlayer();
        HighlightCurrent();
    });

    private void Tab_Click(object sender, RoutedEventArgs e) => ShowTab(ReferenceEquals(sender, TabPlaylist));

    private void ShowTab(bool playlist)
    {
        _playlistTab = playlist;
        TabPhrases.IsChecked = !playlist;
        TabPlaylist.IsChecked = playlist;
        PhrasesPanel.Visibility = playlist ? Visibility.Collapsed : Visibility.Visible;
        PlaylistPanel.Visibility = playlist ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ 曲目列表
    private void FillPlaylist()
    {
        PlList.Items.Clear();
        int i = 0;
        foreach (var item in Pl.Items.OfType<JsonObject>())
        {
            var id = item.Str("id");
            var path = item.Str("path");
            PlList.Items.Add(Row(id, path, ++i));
        }
        PlEmpty.Visibility = Pl.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlList.Visibility = Pl.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        HighlightCurrent();
    }

    private Grid Row(string id, string path, int number)
    {
        var g = new Grid { Tag = id, ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var num = new TextBlock { Text = number.ToString(), Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(path), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(name, path);
        var dur = new TextBlock { Foreground = secondary, VerticalAlignment = VerticalAlignment.Center };
        _durations[path] = dur;
        _ = Task.Run(() =>
        {
            string text;
            try { text = Time(AudioDecode.Duration(path).TotalSeconds); }
            catch { text = T("文件不存在"); }
            A.Post(() => dur.Text = text);
        });
        var remove = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(8, 4, 8, 4),
        };
        ToolTipService.SetToolTip(remove, T("移除"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, T("移除"));
        remove.Click += (_, _) => RemoveTrack(id);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(dur, 2);
        Grid.SetColumn(remove, 3);
        g.Children.Add(num);
        g.Children.Add(name);
        g.Children.Add(dur);
        g.Children.Add(remove);
        g.DoubleTapped += (_, _) =>
        {
            int index = Pl.Items.Select((x, k) => (x?["id"]?.GetValue<string>(), k)).First(t => t.Item1 == id).k;
            Pl.Play(index);
        };
        return g;
    }

    /// <summary>正在放的那首用粗体和主题色。</summary>
    private void HighlightCurrent()
    {
        var current = Pl.CurrentId;
        var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var normal = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        foreach (var g in PlList.Items.OfType<Grid>())
        {
            var name = (TextBlock)g.Children[1];
            bool on = (string)g.Tag == current && (Pl.Playing || Pl.Paused);
            name.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            name.Foreground = on ? accent : normal;
        }
    }

    /// <summary>加到列表末尾；列表里已有的文件（按路径，不分大小写）跳过。</summary>
    private void AddTracks(IEnumerable<string> paths)
    {
        var have = new HashSet<string>(Pl.Items.Select(x => x.Str("path")), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var p in paths)
        {
            if (!have.Add(p)) continue;
            Pl.Items.Add((JsonNode)new JsonObject { ["id"] = ConfigStore.NewId(), ["path"] = p });
            n++;
        }
        if (n == 0) return;
        A.Commit("playlist");
        FillPlaylist();
    }

    private void RemoveTrack(string id)
    {
        var current = Pl.CurrentId;
        var node = Pl.Items.FirstOrDefault(x => x?["id"]?.GetValue<string>() == id);
        if (node == null) return;
        Pl.Items.Remove(node);
        A.Commit("playlist");
        Pl.OnItemsChanged(current);
        FillPlaylist();
    }

    /// <summary>拖动排序完成：按列表里的新顺序重排配置。</summary>
    private void PlList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var current = Pl.CurrentId;
        var byId = Pl.Items.OfType<JsonObject>().ToDictionary(x => x.Str("id"));
        var order = PlList.Items.OfType<Grid>().Select(g => (string)g.Tag).Where(byId.ContainsKey).ToList();
        Pl.Items.Clear();
        foreach (var id in order) Pl.Items.Add((JsonNode)byId[id].DeepClone());
        A.Commit("playlist");
        Pl.OnItemsChanged(current);
        FillPlaylist();
    }

    private async void PlAdd_Click(object sender, RoutedEventArgs e) => AddTracks(await PickAudio(true));

    /// <summary>添加文件夹：包括子文件夹，按路径排序。</summary>
    private async void PlAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;
            var files = await Task.Run(() => Directory
                .EnumerateFiles(folder.Path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => AudioTypes.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList());
            AddTracks(files);
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            A.Main.ShowError(ex.Message);
        }
    }

    /// <summary>导入 .m3u / .m3u8：跳过注释和网址，相对路径按歌单所在文件夹算。</summary>
    private async void PlImport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".m3u8");
        picker.FileTypeFilter.Add(".m3u");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            AddTracks(await Task.Run(() => ReadM3u(file.Path)));
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            A.Main.ShowError(ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] bytes, int count, char[] chars, int charCount);

    private static List<string> ReadM3u(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            // 老的 .m3u 常是系统 ANSI 编码（中文 Windows 上是 GBK）；裁剪后的发布版没有代码页编码，用系统 API 转换
            var chars = new char[bytes.Length];
            text = new string(chars, 0, MultiByteToWideChar(0, 0, bytes, bytes.Length, chars, chars.Length));
        }
        var dir = Path.GetDirectoryName(path) ?? "";
        var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line, UriKind.Absolute, out var uri)) line = uri.LocalPath;
            else if (line.Contains("://")) continue;
            try { line = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(dir, line)); }
            catch { continue; }
            if (AudioTypes.Contains(Path.GetExtension(line).ToLowerInvariant())) list.Add(line);
        }
        return list;
    }

    /// <summary>导出成 .m3u8（UTF-8，绝对路径）。</summary>
    private async void PlExport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = T("歌单") };
        picker.FileTypeChoices.Add(T("M3U 歌单"), [".m3u8"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, A.Main.Hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        var lines = Pl.Items.OfType<JsonObject>().Select(x => x.Str("path")).Where(x => x.Length > 0);
        try
        {
            await File.WriteAllTextAsync(file.Path, "#EXTM3U\r\n" + string.Concat(lines.Select(l => l + "\r\n")), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error(T("导出失败：{0}", ex.Message));
            A.Main.ShowError(T("导出失败：{0}", ex.Message));
        }
    }

    // ------------------------------------------------------------------ 播放控制
    private void PlPlay_Click(object sender, RoutedEventArgs e) => Pl.Toggle();
    private void PlNext_Click(object sender, RoutedEventArgs e) => Pl.Next();
    private void PlPrev_Click(object sender, RoutedEventArgs e) => Pl.Previous();
    private void PlBack_Click(object sender, RoutedEventArgs e) => Pl.Skip(-10);
    private void PlForward_Click(object sender, RoutedEventArgs e) => Pl.Skip(30);

    /// <summary>倍速按钮的文字和菜单（当前值打勾）。</summary>
    private void ShowSpeed()
    {
        PlSpeedText.Text = $"{Pl.Speed:0.##}x";
        PlSpeedMenu.Items.Clear();
        foreach (var sp in Speeds)
        {
            var it = new ToggleMenuFlyoutItem { Text = $"{sp:0.##}x", IsChecked = sp == Pl.Speed };
            it.Click += (_, _) =>
            {
                Pl.Speed = sp;
                ShowSpeed();
            };
            PlSpeedMenu.Items.Add(it);
        }
    }

    private static readonly (string Mode, string Glyph, string Name)[] Modes =
    [
        ("all", "\uE8EE", "列表循环"), // 翻译键
        ("one", "\uE8ED", "单曲循环"), // 翻译键
        ("shuffle", "\uE8B1", "随机播放"), // 翻译键
    ];

    /// <summary>过渡按钮：打开时图标用主题色、旁边显示秒数；菜单里当前值打勾。</summary>
    private void ShowFade()
    {
        int cur = (int)Pl.Crossfade;
        PlFadeText.Text = cur > 0 ? T("{0} 秒", cur) : "";
        PlFadeText.Visibility = cur > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (cur > 0) PlFadeIcon.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        else PlFadeIcon.ClearValue(IconElement.ForegroundProperty);
        PlFadeMenu.Items.Clear();
        foreach (var sec in new[] { 0, 2, 5, 10 })
        {
            var it = new ToggleMenuFlyoutItem { Text = sec == 0 ? T("关") : T("{0} 秒", sec), IsChecked = sec == cur };
            it.Click += (_, _) =>
            {
                A.Set("playlist_crossfade", sec);
                ShowFade();
            };
            PlFadeMenu.Items.Add(it);
        }
    }

    private void PlMode_Click(object sender, RoutedEventArgs e)
    {
        int i = Array.FindIndex(Modes, m => m.Mode == Pl.Mode);
        A.Set("playlist_mode", Modes[(i + 1) % Modes.Length].Mode);
        Pl.PrefetchNext();
        ShowMode();
    }

    /// <summary>模式按钮的图标；名称放在提示里。</summary>
    private void ShowMode()
    {
        var m = Modes.First(x => x.Mode == Pl.Mode);
        PlModeIcon.Glyph = m.Glyph;
        ToolTipService.SetToolTip(PlMode, T(m.Name));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlMode, T(m.Name));
    }

    private void PlSeek_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_seeking || _init) return;
        Pl.Seek(e.NewValue);
        PlPos.Text = Time(e.NewValue);
    }

    /// <summary>播放按钮的图标、名称、封面、进度条。</summary>
    private void UpdatePlayer()
    {
        PlPlayIcon.Glyph = Pl.Playing ? "" : "";
        var tip = Pl.Playing ? T("暂停") : T("播放");
        ToolTipService.SetToolTip(PlPlay, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PlPlay, tip);
        var path = Pl.CurrentPath;
        bool active = path != null && (Pl.Playing || Pl.Paused);
        PlNow.Text = active ? Path.GetFileNameWithoutExtension(path) : "";
        ToolTipService.SetToolTip(PlNow, active ? path : null);
        ShowCover(active ? path : null);
        UpdatePlaylistProgress();
    }

    /// <summary>读音频文件里嵌入的封面（Windows 的音乐缩略图）；读不到时显示音符。</summary>
    private async void ShowCover(string? path)
    {
        if (path == _coverPath) return;
        _coverPath = path;
        BitmapImage? img = null;
        if (path != null && !Covers.TryGetValue(path, out img))
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 240, ThumbnailOptions.UseCurrentScale);
                if (thumb != null && thumb.Type == ThumbnailType.Image)
                {
                    img = new BitmapImage();
                    await img.SetSourceAsync(thumb);
                }
            }
            catch { img = null; }
            Covers[path] = img;
            if (_coverPath != path) return; // 读的时候已经换歌了
        }
        PlCover.Source = img;
        PlCover.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        PlCoverIcon.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdatePlaylistProgress()
    {
        var p = Pl.Progress();
        _seeking = true;
        PlSeek.Maximum = Math.Max(1, p?.Len ?? 1);
        if (!_dragSeek) PlSeek.Value = p?.Pos ?? 0; // 正在拖的时候不抢
        _seeking = false;
        if (!_dragSeek) PlPos.Text = Time(p?.Pos ?? 0);
        PlLen.Text = Time(p?.Len ?? 0);
    }

    private static string Time(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
