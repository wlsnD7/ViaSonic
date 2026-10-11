using System.Runtime.InteropServices;

namespace ViaTTS;

/// <summary>
/// 系统托盘图标。WinUI 3 没有托盘，直接用 Shell_NotifyIcon：在独立线程里建一个隐藏窗口接收托盘消息。
/// 回调在托盘线程触发，调用方自行切回界面线程。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public const string WindowClass = "ViaTTS_TrayWindow";
    /// <summary>第二个实例启动时发这个消息，让已运行的实例显示主窗口。</summary>
    public const uint WM_SHOW_MAIN = 0x8000 + 1;
    private const uint WM_TRAY = 0x8000 + 2;
    private const uint WM_BALLOON = 0x8000 + 3;
    private const uint WM_REMOVE = 0x8000 + 4; // 程序退出时删除图标；外部发来的 WM_CLOSE 一律忽略
    private const uint WM_DESTROY = 0x0002, WM_CLOSE = 0x0010;
    private const uint WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    private const int MenuOpen = 1, MenuExit = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Native.MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Native.MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Native.MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    private readonly WndProcDelegate _wndProc; // 持有引用，防止被 GC
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hwnd, _icon;
    private uint _taskbarCreated;
    private (string Title, string Text)? _pendingBalloon;

    public TrayIcon()
    {
        _wndProc = WndProc;
        new Thread(Loop) { IsBackground = true, Name = "tray" }.Start();
        _ready.Wait(5000);
    }

    private void Loop()
    {
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = _wndProc,
            lpszClassName = WindowClass,
        };
        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0, WindowClass, "ViaTTS Tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "ViaTTS.ico");
        int size = GetSystemMetrics(49); // SM_CXSMICON
        _icon = LoadImageW(IntPtr.Zero, ico, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        AddIcon();
        _ready.Set();
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
        szTip = "ViaTTS",
        szInfo = "",
        szInfoTitle = "",
    };

    private void AddIcon()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIconW(NIM_ADD, ref d);
    }

    /// <summary>托盘气泡提示（Windows 11 上显示为通知）。</summary>
    public void ShowBalloon(string title, string text)
    {
        _pendingBalloon = (title, text);
        PostMessageW(_hwnd, WM_BALLOON, IntPtr.Zero, IntPtr.Zero);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, (IntPtr)MenuOpen, "打开 ViaTTS");
        AppendMenuW(menu, 0x800 /* MF_SEPARATOR */, IntPtr.Zero, null);
        AppendMenuW(menu, 0, (IntPtr)MenuExit, "退出");
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // 否则点别处时菜单不会消失
        int cmd = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */ | 0x0020 /* TPM_BOTTOMALIGN */, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == MenuOpen) OpenRequested?.Invoke();
        else if (cmd == MenuExit) ExitRequested?.Invoke();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAY)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) OpenRequested?.Invoke();
            else if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == WM_SHOW_MAIN)
        {
            OpenRequested?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == WM_BALLOON && _pendingBalloon is { } b)
        {
            var d = Data(NIF_INFO);
            d.szInfoTitle = b.Title;
            d.szInfo = b.Text;
            d.dwInfoFlags = 1; // NIIF_INFO
            Shell_NotifyIconW(NIM_MODIFY, ref d);
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated) // 资源管理器重启后托盘图标会丢，重新加
        {
            AddIcon();
            return IntPtr.Zero;
        }
        if (msg == WM_CLOSE) return IntPtr.Zero;
        if (msg == WM_REMOVE)
        {
            var d = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref d);
            DestroyWindow(hWnd);
            return IntPtr.Zero;
        }
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        // 同步等托盘线程删掉图标，否则进程退出后托盘里会残留一个图标
        if (_hwnd != IntPtr.Zero) SendMessageW(_hwnd, WM_REMOVE, IntPtr.Zero, IntPtr.Zero);
        _hwnd = IntPtr.Zero;
    }
}
