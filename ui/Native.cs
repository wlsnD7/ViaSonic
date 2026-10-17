using System.Runtime.InteropServices;

namespace ViaSonic;

internal static partial class Native
{
    public const int MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint WM_HOTKEY = 0x0312, WM_APP = 0x8000, WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vk);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND { public uint dwFlags; public int fEnable; public IntPtr hRgnBlur; public int fTransitionOnMaximized; }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND bb);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int l, int t, int r, int b);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    /// <summary>让窗口按像素透明：模糊区域设成窗口外的一个空矩形，DWM 就按内容的 alpha 合成而不模糊。</summary>
    public static void EnablePerPixelAlpha(IntPtr hwnd)
    {
        var rgn = CreateRectRgn(-2, -2, -1, -1);
        var bb = new DWM_BLURBEHIND { dwFlags = 0x1 | 0x2 /* DWM_BB_ENABLE | DWM_BB_BLURREGION */, fEnable = 1, hRgnBlur = rgn };
        DwmEnableBlurBehindWindow(hwnd, ref bb);
        DeleteObject(rgn);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);

    /// <summary>让系统重新计算窗口边框（WM_NCCALCSIZE）。</summary>
    public static void FrameChanged(IntPtr hwnd) =>
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010 /* FRAMECHANGED | NOSIZE | NOMOVE | NOZORDER | NOACTIVATE */);

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    [LibraryImport("CoreMessaging.dll")]
    private static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    private static IntPtr _dqController;

    /// <summary>Windows.UI.Composition 要求当前线程有 Windows.System.DispatcherQueue，WinUI 线程默认没有。</summary>
    public static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || _dqController != IntPtr.Zero) return;
        var o = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* DQTYPE_THREAD_CURRENT */, apartmentType = 2 /* DQTAT_COM_STA */ };
        CreateDispatcherQueueController(o, out _dqController);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT p);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWndProc(IntPtr hWnd, int index, WndProc proc);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    public static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetFocus();

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetFocus(IntPtr hWnd);




    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder sb, int max);

    public static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return T("（无）");
        var sb = new System.Text.StringBuilder(128);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return $"{sb}@{hwnd:X}";
    }


    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateMutex(IntPtr attrs, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>绕过前台锁：附加到当前前台线程的输入队列后再切换。</summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (SetForegroundWindow(hwnd)) return true;
        var fg = GetForegroundWindow();
        uint fgTid = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
        uint me = GetCurrentThreadId();
        bool attached = fgTid != 0 && fgTid != me && AttachThreadInput(me, fgTid, true);
        try
        {
            BringWindowToTop(hwnd);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgTid, false);
        }
    }
}
