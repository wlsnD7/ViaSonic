using System.Runtime.InteropServices;

namespace ViaTTS.Audio;

/// <summary>按键说话：朗读期间模拟按住游戏的说话键。用扫描码发送，DirectInput / RawInput 的游戏才能识别。</summary>
public static partial class Ptt
{
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint n, [In] INPUT[] inputs, int size);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint code, uint mapType);

    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100;
    private static readonly HashSet<int> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x6F];

    /// <summary>规范化按键名；不认识时抛异常。允许：键盘单键、mouse3/4/5。</summary>
    public static string Parse(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return "";
        if (t is "mouse3" or "mouse4" or "mouse5" || HotkeyText.Vk.ContainsKey(t)) return t;
        throw new HotkeyException(T("无法识别的按键：{0}", text));
    }

    public static void Press(string name, bool down)
    {
        if (string.IsNullOrEmpty(name)) return;
        var inp = new INPUT();
        if (name.StartsWith("mouse"))
        {
            inp.type = 0;
            if (name == "mouse3") inp.u.mi.dwFlags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
            else
            {
                inp.u.mi.mouseData = name == "mouse4" ? 1u : 2u;
                inp.u.mi.dwFlags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP;
            }
        }
        else if (HotkeyText.Vk.TryGetValue(name, out var vk))
        {
            inp.type = 1;
            inp.u.ki.wVk = (ushort)vk;
            inp.u.ki.wScan = (ushort)MapVirtualKey((uint)vk, 0);
            inp.u.ki.dwFlags = KEYEVENTF_SCANCODE | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP);
        }
        else return;
        SendInput(1, [inp], Marshal.SizeOf<INPUT>());
    }
}
