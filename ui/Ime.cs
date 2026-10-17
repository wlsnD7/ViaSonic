using System.Runtime.InteropServices;

namespace ViaSonic;

/// <summary>
/// 输入法状态。游戏（如 Unity）会在自己的窗口里关掉输入法，焦点刚切到输入框时微软拼音还停在关闭 / 英文状态，
/// 开头几个键会直接变成英文字母。弹出输入框时主动把输入法打开并切到中文。
/// </summary>
public static partial class Ime
{
    [LibraryImport("imm32.dll")] private static partial IntPtr ImmGetContext(IntPtr hwnd);
    [LibraryImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ImmReleaseContext(IntPtr hwnd, IntPtr himc);
    [LibraryImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ImmGetOpenStatus(IntPtr himc);
    [LibraryImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ImmSetOpenStatus(IntPtr himc, [MarshalAs(UnmanagedType.Bool)] bool open);
    [LibraryImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ImmGetConversionStatus(IntPtr himc, out uint conversion, out uint sentence);
    [LibraryImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ImmSetConversionStatus(IntPtr himc, uint conversion, uint sentence);
    [LibraryImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ImmAssociateContextEx(IntPtr hwnd, IntPtr himc, uint flags);
    [LibraryImport("user32.dll")] private static partial IntPtr GetKeyboardLayout(uint threadId);

    private const uint IME_CMODE_NATIVE = 0x1;
    private const uint IACE_DEFAULT = 0x10;


    /// <summary>打开输入法并切到中文（只在当前键盘布局是中文时有意义，其他语言不动）。</summary>
    public static void OpenNative(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        uint tid = Native.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        long lang = GetKeyboardLayout(tid).ToInt64() & 0x3FF; // 主语言 ID
        if (lang != 0x04) return; // 只处理中文
        var himc = ImmGetContext(hwnd);
        if (himc == IntPtr.Zero)
        {
            ImmAssociateContextEx(hwnd, IntPtr.Zero, IACE_DEFAULT); // 被禁用过的话恢复默认上下文
            himc = ImmGetContext(hwnd);
            if (himc == IntPtr.Zero) return;
        }
        try
        {
            if (!ImmGetOpenStatus(himc)) ImmSetOpenStatus(himc, true);
            if (ImmGetConversionStatus(himc, out var conv, out var sent) && (conv & IME_CMODE_NATIVE) == 0)
                ImmSetConversionStatus(himc, conv | IME_CMODE_NATIVE, sent);
        }
        finally { ImmReleaseContext(hwnd, himc); }
    }
}
