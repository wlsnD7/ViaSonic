using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace ViaTTS;

/// <summary>
/// 快捷键呼出的半透明输入框：只有一个文本框，回车朗读。
/// 回车后这句话留在框里显示，播完清空；只有再按快捷键，或开启「播完后收起」时播完，才会收起。
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly TextBox _box;
    private readonly IntPtr _hwnd;
    private IntPtr _prev, _site, _prevWndProc;
    private bool _visible, _closing;
    private bool _lingering, _sawBusy; // 已发送、等播完再清空
    private bool _awaiting;            // 刚弹出、等系统挪走焦点再允许输入
    private DateTime _lingerStart;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _lingerTimer, _awaitTimer;
    private Native.WndProc? _wndProc; // 持有引用，防止被 GC

    private static App A => App.Current;

    public OverlayWindow()
    {
        _box = new TextBox
        {
            FontSize = 16,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(14, 8, 14, 8),
            IsSpellCheckEnabled = false,
        };
        // 去掉 TextBox 获得焦点时的底色和下划线，保持只是一个干净的框
        foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver", "TextControlBackground" })
            _box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "TextControlBorderBrushFocused", "TextControlBorderBrushPointerOver", "TextControlBorderBrush" })
            _box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        _box.KeyDown += OnKeyDown;
        _box.Loaded += (_, _) => HideClearButton(_box);

        Content = new Grid { Children = { _box } };
        SystemBackdrop = new DesktopAcrylicBackdrop();
        Title = "ViaTTS 输入";

        _hwnd = WindowNative.GetWindowHandle(this);
        var p = OverlappedPresenter.CreateForToolWindow();
        p.IsAlwaysOnTop = true;
        p.IsResizable = false;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        p.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;
        int round = 2; // DWMWCP_ROUND
        Native.DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));

        A.StateChanged += OnStateChanged;
        AppWindow.Closing += (s, e) =>
        {
            // 主程序退出前只隐藏，避免被用户 Alt+F4 关掉后再也呼不出来
            if (!_closing) { e.Cancel = true; Hide(true); }
        };
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
    /// 输入框正在用（有焦点）→ 收起；否则显示 / 把焦点拉回来。
    /// 点回游戏后输入框会留着，再按快捷键就能继续输入；播放中按则直接输入下一句。
    /// </summary>
    public void Toggle(IntPtr prev)
    {
        if (_visible && !_lingering && prev == _hwnd) Hide(true);
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
        var o = A.Sub("overlay");
        double ratio = Math.Clamp(App.ToDouble(o["width_ratio"]), 0.1, 0.95);
        int bottom = (int)App.ToDouble(o["bottom_offset"]);

        // 显示在游戏所在的屏幕上
        var anchor = _prev != IntPtr.Zero ? Win32Interop.GetWindowIdFromWindow(_prev) : A.Main.AppWindow.Id;
        var area = DisplayArea.GetFromWindowId(anchor, DisplayAreaFallback.Primary);
        var b = area.OuterBounds;
        double scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        int w = (int)(b.Width * ratio);
        int h = (int)(48 * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            b.X + (b.Width - w) / 2, b.Y + b.Height - h - (int)(bottom * scale), w, h));

        if (!_visible || _lingering) _box.Text = ""; // 只是把焦点拉回来时保留已经打的字
        StopLinger();
        _visible = true;
        Native.SetWindowOpacity(_hwnd, o["opacity"] is { } op ? App.ToDouble(op) : 0.8);
        AppWindow.Show(true);
        Activate();
        Native.ForceForeground(_hwnd);
        _box.Focus(FocusState.Programmatic);
        _box.SelectionStart = _box.Text.Length;
        _site = ContentInputSite();
        Ime.OpenNative(_site);
        HookOuterFocus();
        BeginAwaitFocus();
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
        if (restore && (A.Sub("overlay")["restore_focus"]?.GetValue<bool>() ?? true) && _prev != IntPtr.Zero)
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
        _awaitTimer.Tick += (_, _) => EndAwaitFocus();
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

    /// <summary>接管外层窗口的 WM_SETFOCUS：系统把焦点设到外层窗口时交还给 WinUI 内容子窗口。</summary>
    private void HookOuterFocus()
    {
        if (_wndProc != null) return;
        _wndProc = (h, msg, w, l) =>
        {
            var r = Native.CallWindowProc(_prevWndProc, h, msg, w, l);
            if (msg == 0x0007 /* WM_SETFOCUS */ && _visible && _site != IntPtr.Zero && Native.GetFocus() == _hwnd)
            {
                Native.SetFocus(_site);
                if (_awaiting) DispatcherQueue.TryEnqueue(EndAwaitFocus);
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
        _lingering = true;
        _sawBusy = false;
        if ((o["restore_focus"]?.GetValue<bool>() ?? true) && _prev != IntPtr.Zero) Native.ForceForeground(_prev);
        _lingerTimer = DispatcherQueue.CreateTimer();
        _lingerTimer.Interval = TimeSpan.FromSeconds(4);
        _lingerTimer.IsRepeating = true;
        _lingerTimer.Tick += (_, _) =>
        {
            // 4 秒内都没开始合成（比如引擎没就绪），或者超过 2 分钟，都直接结束
            if (!_sawBusy || DateTime.UtcNow - _lingerStart > TimeSpan.FromMinutes(2)) EndLinger();
        };
        _lingerStart = DateTime.UtcNow;
        _lingerTimer.Start();
        A.Speaker.Speak(text);
    }

    private void OnStateChanged()
    {
        if (!_lingering) return;
        var busy = A.State["busy"]?.GetValue<string>() ?? "";
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
    }

    /// <summary>播完了：清空文字；开了「播完后收起」就收起（焦点已经在游戏里，不再切换）。</summary>
    private void EndLinger()
    {
        if (!_lingering) return;
        StopLinger();
        _box.Text = "";
        if (A.Sub("overlay")["hide_after_send"]?.GetValue<bool>() ?? true)
        {
            _visible = false;
            AppWindow.Hide();
        }
    }
}
