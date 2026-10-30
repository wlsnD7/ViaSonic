using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace ViaSonic;

/// <summary>
/// 快捷键呼出的半透明输入框：一个文本框，回车或点发送按钮朗读；文字多了向上展开，超过最大行数后滚动。
/// 发送后这句话留在框里显示，播完清空；只有再按快捷键，或开启「播完后收起」时播完，才会收起。
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private const double MinHeightDip = 48; // 一行时输入框的高度
    private const double BoxMargin = 5;     // 文本框上下各留的空白

    private readonly TextBox _box;
    private readonly Button _send;
    private readonly Grid _root;
    private double _height = MinHeightDip;  // 当前窗口高度（DIP），跟着输入框的行数变化
    private readonly IntPtr _hwnd;
    private IntPtr _prev, _site, _prevWndProc;
    private bool _visible, _closing;
    private bool _lingering, _sawBusy; // 已发送、等播完再清空
    private bool _awaiting;            // 刚弹出、等系统挪走焦点再允许输入
    private DateTime _lingerStart;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lingerTimer, _awaitTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _dragTimer; // 按住把手期间跟着鼠标移动
    private Native.WndProc? _wndProc; // 持有引用，防止被 GC

    private static App A => App.Current;

    /// <summary>拖动输入框后位置已经写进配置（设置页刷新显示）。</summary>
    public event Action? Moved;

    public OverlayWindow()
    {
        _box = new TextBox
        {
            FontSize = 16,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, BoxMargin, 0, BoxMargin),
            Padding = new Thickness(14, 8, 14, 8),
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        // 去掉 TextBox 获得焦点时的底色和下划线，保持只是一个干净的框
        foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBackground" })
            _box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "TextControlBorderBrushFocused", "TextControlBorderBrushPointerOver", "TextControlBorderBrush" })
            _box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        _box.KeyDown += OnKeyDown;
        _box.Loaded += (_, _) =>
        {
            HideClearButton(_box);
            _scroll = FindScroll(_box);
            // 换行要等下一轮排版，内容变高时再滚一次
            _scroll?.RegisterPropertyChangedCallback(ScrollViewer.ExtentHeightProperty, (_, _) => ScrollToEnd());
        };
        // 输入法拼写中的文字不会让文本框滚动，超出范围后新打的字看不到：光标在末尾时滚到底（一行时滚到最右）
        _box.TextChanged += (_, _) => ScrollToEnd();
        _box.SelectionChanged += (_, _) => ScrollToEnd();
        _box.TextCompositionChanged += (_, _) => ScrollToEnd();
        _box.TextCompositionEnded += (_, _) => ScrollToEnd();
        Grid.SetColumn(_box, 1);

        // 右下角发送按钮：和回车一样；点击不抢文本框的焦点
        _send = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 8),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            IsTabStop = false,
            AllowFocusOnInteraction = false,
            Content = new FontIcon { Glyph = "", FontSize = 16 },
        };
        foreach (var key in new[] { "ButtonBackgroundDisabled", "ButtonBorderBrushDisabled" })
            _send.Resources[key] = new SolidColorBrush(Colors.Transparent);
        ToolTipService.SetToolTip(_send, T("发送"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_send, T("发送"));
        _send.Click += (_, _) => Send();
        Grid.SetColumn(_send, 2);

        // 左侧把手：按住拖动输入框
        var grip = new Grip
        {
            Width = 28,
            Background = new SolidColorBrush(Colors.Transparent),
            Children = { new FontIcon { Glyph = "\uE784", FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] } },
        };
        ToolTipService.SetToolTip(grip, T("拖动"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grip, T("拖动"));
        grip.PointerPressed += OnGripPressed;

        // 窗口本身全透明，只有这层背景按「不透明度」半透明，文字和光标不受影响。
        // 这层按文字行数自己定高度，窗口随后再跟着变高变矮（底边不动）
        _root = new Grid
        {
            CornerRadius = new CornerRadius(8),
            MinHeight = MinHeightDip,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { grip, _box, _send },
        };
        _root.ActualThemeChanged += (_, _) => ApplyBackground();
        _root.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - _height) < 0.5) return;
            _height = e.NewSize.Height;
            if (_visible) Place();
        };
        Content = new FreeHeightPanel { Children = { _root } };
        Title = T("ViaSonic 输入");

        _hwnd = WindowNative.GetWindowHandle(this);
        var p = OverlappedPresenter.CreateForToolWindow();
        p.IsAlwaysOnTop = true;
        p.IsResizable = false;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        p.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;
        Native.EnablePerPixelAlpha(_hwnd);
        HookWndProc();
        Native.FrameChanged(_hwnd);
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE：不要 Windows 11 的 1px 系统边线
        Native.DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));
        SystemBackdrop = new TransparentBackdrop();
        ApplyBackground();
        ApplyLayout();

        A.StateChanged += OnStateChanged;
        AppWindow.Closing += (s, e) =>
        {
            // 主程序退出前只隐藏，避免被用户 Alt+F4 关掉后再也呼不出来
            if (!_closing) { e.Cancel = true; Hide(true); }
        };
    }

    private ScrollViewer? _scroll;
    private bool _scrollPending;

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer { Name: "ContentElement" } sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>等这次输入排版完，光标在末尾时把文本框滚到底（不换行时滚到最右）。</summary>
    private void ScrollToEnd()
    {
        if (_scroll == null || _scrollPending) return;
        _scrollPending = true;
        A.Post(() =>
        {
            _scrollPending = false;
            if (_scroll == null || _box.SelectionStart + _box.SelectionLength < _box.Text.Length) return;
            _scroll.UpdateLayout();
            if (_box.TextWrapping == TextWrapping.NoWrap) _scroll.ChangeView(_scroll.ScrollableWidth, null, null, true);
            else _scroll.ChangeView(null, _scroll.ScrollableHeight, null, true);
        });
    }

    /// <summary>
    /// 不按窗口当前高度限制子元素：窗口跟着变高变矮要慢一两帧，这期间输入框照样是新高度（不会挤出滚动条）。
    /// 比窗口高（正在变高）时贴顶，原来的文字先停在原处，窗口长高后再一起上移；比窗口矮时贴底。
    /// 变矮时系统常先按新大小画、过一两帧才挪窗口，文字仍会轻微下落一下。
    /// </summary>
    private sealed partial class FreeHeightPanel : Panel
    {
        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
        {
            foreach (var c in Children) c.Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
            return new Windows.Foundation.Size(available.Width, double.IsInfinity(available.Height) ? 0 : available.Height);
        }

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
        {
            foreach (var c in Children)
            {
                double h = c.DesiredSize.Height;
                c.Arrange(new Windows.Foundation.Rect(0, Math.Max(0, final.Height - h), final.Width, h));
            }
            return final;
        }
    }

    /// <summary>发送按钮显隐，以及按最大行数限制文本框高度。</summary>
    private void ApplyLayout()
    {
        var o = A.Sub("overlay");
        bool send = o.Bool("send_button", true);
        _send.Visibility = send ? Visibility.Visible : Visibility.Collapsed;
        _box.Padding = new Thickness(14, 8, send ? 6 : 14, 8);
        int lines = (int)Math.Clamp(o["max_lines"] is { } v ? Json.ToDouble(v) : 5, 1, 10);
        // 只有一行时不换行，字多了往左滚
        _box.TextWrapping = lines == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        // 行高是字号的 4/3（实测中英文都是 21.33）；用游离的 TextBlock 去量有时只量出一行
        _box.MaxHeight = Math.Ceiling(lines * _box.FontSize * 4 / 3) + _box.Padding.Top + _box.Padding.Bottom;
    }

    /// <summary>TextBox 模板自带的"×"清除按钮由视觉状态控制显隐，这里把它压成 0 宽且不可点。</summary>
    private static void HideClearButton(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button { Name: "DeleteButton" } b)
            {
                b.MaxWidth = 0;
                b.Opacity = 0;
                b.IsHitTestVisible = false;
                b.IsTabStop = false;
                return;
            }
            HideClearButton(child);
        }
    }

    public new void Close()
    {
        _closing = true;
        base.Close();
    }

    // ------------------------------------------------------------------ 显示 / 收起
    /// <summary>
    /// 开着 → 收起（焦点在不在输入框都一样，把焦点拉回来用「聚焦输入框」）；没开 → 显示。
    /// 播放中按则直接输入下一句。
    /// </summary>
    public void Toggle(IntPtr prev)
    {
        // 焦点已经在别的窗口时只收起，不再切回呼出前的窗口
        if (_visible && !_lingering) Hide(prev == _hwnd);
        else Show(prev);
    }

    /// <summary>「聚焦输入框」快捷键：输入框开着时把焦点从游戏拉回来，没开时不做任何事。</summary>
    public void FocusIfVisible(IntPtr prev)
    {
        if (_visible && prev != _hwnd) Show(prev);
    }

    public void Show(IntPtr prev)
    {
        if (prev != _hwnd && prev != IntPtr.Zero) _prev = prev;
        if (prev == IntPtr.Zero) _prev = IntPtr.Zero;
        if (!_visible || _lingering) // 只是把焦点拉回来时保留已经打的字
        {
            _box.Text = "";
            _height = MinHeightDip;
        }
        Place();
        StopLinger();
        _visible = true;
        ApplyBackground();
        AppWindow.Show(true);
        Activate();
        Native.ForceForeground(_hwnd);
        _box.Focus(FocusState.Programmatic);
        _box.SelectionStart = _box.Text.Length;
        _site = ContentInputSite();
        Ime.OpenNative(_site);
        if (A.Sub("overlay").Bool("await_focus", true)) BeginAwaitFocus();
    }

    /// <summary>输入框的设置改了：显示着的话立即按新设置摆放。</summary>
    public void OnConfig()
    {
        ApplyBackground();
        ApplyLayout();
        if (_visible) Place();
    }

    // ------------------------------------------------------------------ 位置与背景
    /// <summary>显示在游戏所在的屏幕上：水平位置按屏幕宽度比例（输入框中心），底部距离按 DPI 缩放，不超出屏幕。</summary>
    private void Place()
    {
        var o = A.Sub("overlay");
        double ratio = Math.Clamp(o.Num("width_ratio"), 0.1, 0.95);
        double xr = o["x_ratio"] is { } x ? Math.Clamp(Json.ToDouble(x), 0, 1) : 0.5;
        int bottom = (int)o.Num("bottom_offset");
        // 呼出前的窗口可能已经关了（游戏退出）：这时 GetFromWindowId 返回 null，改按主窗口所在的屏幕
        var anchor = _prev != IntPtr.Zero && Native.IsWindow(_prev) ? Win32Interop.GetWindowIdFromWindow(_prev) : A.Main.AppWindow.Id;
        var b = (DisplayArea.GetFromWindowId(anchor, DisplayAreaFallback.Primary) ?? DisplayArea.Primary).OuterBounds;
        double scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        int w = (int)(b.Width * ratio);
        int h = (int)Math.Ceiling(_height * scale);
        int left = Math.Clamp(b.X + (int)(b.Width * xr) - w / 2, b.X, b.X + b.Width - w);
        int top = Math.Clamp(b.Y + b.Height - h - (int)(bottom * scale), b.Y, b.Y + b.Height - h);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(left, top, w, h));
    }

    private void ApplyBackground()
    {
        var o = A.Sub("overlay");
        double op = o["opacity"] is { } v ? Math.Clamp(Json.ToDouble(v), 0.2, 1) : 0.75;
        byte alpha = (byte)Math.Round(op * 255);
        bool dark = _root.ActualTheme == ElementTheme.Dark;
        byte c = dark ? (byte)0x20 : (byte)0xF3;
        _root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, c, c, c));
    }

    /// <summary>
    /// 按住把手拖动：按下时记下鼠标和窗口的屏幕坐标，之后每 10ms 跟着鼠标平移，左键松开时保存。
    /// 不用指针捕获：窗口被点击激活时捕获会立刻丢失。
    /// </summary>
    private void OnGripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed || _dragTimer != null) return;
        e.Handled = true;
        Native.GetCursorPos(out var from);
        var win = AppWindow.Position;
        _dragTimer = DispatcherQueue.CreateTimer();
        _dragTimer.Interval = TimeSpan.FromMilliseconds(10);
        _dragTimer.Tick += (_, _) => App.Guard(() =>
        {
            Native.GetCursorPos(out var pt);
            AppWindow.Move(new Windows.Graphics.PointInt32(win.X + pt.X - from.X, win.Y + pt.Y - from.Y));
            if (!Native.IsDown(0x01 /* VK_LBUTTON */)) EndDrag();
        });
        _dragTimer.Start();
    }

    /// <summary>松开：把位置换算成水平比例（输入框中心）和底部距离保存。</summary>
    private void EndDrag()
    {
        _dragTimer?.Stop();
        _dragTimer = null;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        var b = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).OuterBounds;
        double scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        var o = A.Sub("overlay");
        o["x_ratio"] = Math.Round(Math.Clamp((pos.X + size.Width / 2.0 - b.X) / b.Width, 0, 1), 3);
        o["bottom_offset"] = (int)Math.Clamp((b.Y + b.Height - pos.Y - size.Height) / scale, 0, 2000);
        A.Commit("overlay");
        Moved?.Invoke();
        if (_visible && !_lingering && !_awaiting) _box.Focus(FocusState.Programmatic);
    }

    /// <summary>把手上显示移动光标（ProtectedCursor 只能在子类里设置）。</summary>
    private sealed partial class Grip : Grid
    {
        public Grip() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }

    /// <summary>不画任何背景的 SystemBackdrop，配合 EnablePerPixelAlpha 让窗口透出后面的画面。</summary>
    private sealed partial class TransparentBackdrop : SystemBackdrop
    {
        private static Windows.UI.Composition.Compositor? _compositor;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnTargetConnected(target, root);
            Native.EnsureSystemDispatcherQueue();
            _compositor ??= new Windows.UI.Composition.Compositor();
            target.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            base.OnTargetDisconnected(target);
            target.SystemBackdrop = null;
        }
    }

    public void Hide(bool restore)
    {
        if (!_visible) return;
        _awaiting = false;
        _awaitTimer?.Stop();
        StopLinger();
        _box.Text = "";
        _visible = false;
        AppWindow.Hide();
        if (restore && A.Sub("overlay").Bool("restore_focus", true) && _prev != IntPtr.Zero)
            Native.ForceForeground(_prev);
    }

    // ------------------------------------------------------------------ 焦点
    /// <summary>
    /// 从游戏呼出后约 0.6~1 秒，游戏线程响应激活通知较慢，系统会把键盘焦点挪到外层窗口一次
    /// （WinUI 仍以为文本框有焦点，但按键不再经过它：光标消失、拼音变英文字母）。
    /// 在那之前先禁止输入（只读、变暗），焦点被挪走并抢回后再允许输入；1.2 秒内没发生就直接放开。
    /// </summary>
    private void BeginAwaitFocus()
    {
        _awaiting = true;
        _box.IsReadOnly = true;
        _box.Opacity = 0.45;
        _awaitTimer?.Stop();
        _awaitTimer = DispatcherQueue.CreateTimer();
        _awaitTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _awaitTimer.IsRepeating = false;
        _awaitTimer.Tick += (_, _) => App.Guard(EndAwaitFocus);
        _awaitTimer.Start();
    }

    private void EndAwaitFocus()
    {
        if (!_awaiting || !_visible) return;
        _awaiting = false;
        _awaitTimer?.Stop();
        if (_lingering) return;
        _box.IsReadOnly = false;
        _box.Opacity = 1;
        _box.Focus(FocusState.Programmatic);
        _box.SelectionStart = _box.Text.Length;
    }

    /// <summary>
    /// 接管外层窗口：
    /// WM_NCCALCSIZE 返回 0，整个窗口都是内容区（没有标题栏时系统仍会留 3px 边框，显示时还会被加回来）；
    /// WM_SETFOCUS 时把焦点交还给 WinUI 内容子窗口。
    /// </summary>
    private void HookWndProc()
    {
        _wndProc = (h, msg, w, l) =>
        {
            if (msg == 0x0083 /* WM_NCCALCSIZE */ && w != IntPtr.Zero) return IntPtr.Zero;
            var r = Native.CallWindowProc(_prevWndProc, h, msg, w, l);
            if (msg == 0x0007 /* WM_SETFOCUS */ && _visible && _site != IntPtr.Zero && Native.GetFocus() == _hwnd)
            {
                Native.SetFocus(_site);
                if (_awaiting) A.Post(EndAwaitFocus);
            }
            return r;
        };
        _prevWndProc = Native.SetWndProc(_hwnd, -4 /* GWLP_WNDPROC */, _wndProc);
    }

    /// <summary>键盘焦点应在的 WinUI 输入子窗口。</summary>
    private IntPtr ContentInputSite()
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumChildWindows(_hwnd, (h, _) =>
        {
            if (Native.ClassOf(h).StartsWith("InputSiteWindowClass")) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ------------------------------------------------------------------ 发送
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Send();
    }

    private void Send()
    {
        if (_lingering) return;
        var text = _box.Text.Trim();
        if (text.Length == 0) return;
        var o = A.Sub("overlay");
        // 这句话留在框里显示，播完清空。归还焦点时先切回游戏再朗读（按键说话键需要游戏在前台才能收到）
        _box.Text = text;
        _box.IsReadOnly = true;
        _box.Opacity = 0.65;
        _send.IsEnabled = false;
        _lingering = true;
        _sawBusy = false;
        if (o.Bool("restore_focus", true) && _prev != IntPtr.Zero) Native.ForceForeground(_prev);
        _lingerTimer = DispatcherQueue.CreateTimer();
        _lingerTimer.Interval = TimeSpan.FromSeconds(4);
        _lingerTimer.IsRepeating = true;
        _lingerTimer.Tick += (_, _) => App.Guard(() =>
        {
            // 4 秒内都没开始合成（比如引擎没就绪），或者超过 2 分钟，都直接结束
            if (!_sawBusy || DateTime.UtcNow - _lingerStart > TimeSpan.FromMinutes(2)) EndLinger();
        });
        _lingerStart = DateTime.UtcNow;
        _lingerTimer.Start();
        A.Speaker.Speak(text);
    }

    private void OnStateChanged()
    {
        if (!_lingering) return;
        var busy = A.State.Busy;
        if (busy.Length > 0) _sawBusy = true;
        else if (_sawBusy) EndLinger();
    }

    private void StopLinger()
    {
        _lingering = false;
        _lingerTimer?.Stop();
        _lingerTimer = null;
        _box.IsReadOnly = false;
        _box.Opacity = 1;
        _send.IsEnabled = true;
    }

    /// <summary>播完了：清空文字；开了「播完后收起」就收起（焦点已经在游戏里，不再切换）。</summary>
    private void EndLinger()
    {
        if (!_lingering) return;
        StopLinger();
        _box.Text = "";
        if (A.Sub("overlay").Bool("hide_after_send", true))
        {
            _visible = false;
            AppWindow.Hide();
        }
    }
}
