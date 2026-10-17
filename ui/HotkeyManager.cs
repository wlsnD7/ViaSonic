using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ViaSonic;

/// <summary>
/// 全局快捷键。
/// 普通快捷键用 RegisterHotKey：只拦截完全相同的组合，其余按键原样交给游戏。
/// 单独按一下 Alt / Ctrl / Shift 时用低级键盘钩子检测（按下后中间没按别的键就松开），只在有这类绑定时才安装，不拦截按键。
/// 在独立线程跑消息循环，回调也在该线程里触发。
/// </summary>
/// <summary>快捷键错误：Message 是已经翻译好的原因；ConflictWith 不为空时表示和这个绑定重复，显示时再生成文字。</summary>
public record HotkeyError(string Message, string? ConflictWith = null);

public sealed partial class HotkeyManager : IDisposable
{
    private readonly Action<string> _onHotkey;
    private readonly ConcurrentQueue<(Dictionary<string, string> Bindings, TaskCompletionSource<Dictionary<string, HotkeyError>> Done)> _ops = new();
    private readonly Dictionary<int, string> _ids = [];
    private readonly Dictionary<string, string> _taps = []; // 修饰键 → 绑定名
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;

    public HotkeyManager(Action<string> onHotkey)
    {
        _onHotkey = onHotkey;
        _hookProc = HookProc;
        new Thread(Loop) { IsBackground = true, Name = "hotkeys" }.Start();
        _ready.Wait(5000);
    }

    /// <summary>替换全部绑定，返回 {名称: 错误}。</summary>
    public Dictionary<string, HotkeyError> SetBindings(Dictionary<string, string> bindings)
    {
        var tcs = new TaskCompletionSource<Dictionary<string, HotkeyError>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ops.Enqueue((bindings, tcs));
        Native.PostThreadMessage(_threadId, Native.WM_APP, IntPtr.Zero, IntPtr.Zero);
        return tcs.Task.Wait(5000) ? tcs.Task.Result : [];
    }

    private Dictionary<string, HotkeyError> Apply(Dictionary<string, string> bindings)
    {
        foreach (var id in _ids.Keys) Native.UnregisterHotKey(IntPtr.Zero, id);
        _ids.Clear();
        _taps.Clear();
        var errors = new Dictionary<string, HotkeyError>();
        var used = new Dictionary<string, string>();
        int nextId = 1;
        foreach (var (name, text) in bindings)
        {
            if (string.IsNullOrEmpty(text)) continue;
            try
            {
                var (mods, vk, norm) = HotkeyText.Parse(text);
                if (used.TryGetValue(norm, out var other)) { errors[name] = new HotkeyError("", other); continue; }
                if (vk == 0) _taps[norm] = name;
                else if (!Native.RegisterHotKey(IntPtr.Zero, nextId, mods | Native.MOD_NOREPEAT, vk))
                {
                    errors[name] = new HotkeyError(T("已被占用"));
                    continue;
                }
                else _ids[nextId++] = name;
                used[norm] = name;
            }
            catch (HotkeyException e) { errors[name] = new HotkeyError(e.Message); }
        }
        UpdateHook();
        return errors;
    }

    private void Loop()
    {
        _threadId = Native.GetCurrentThreadId();
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // 创建消息队列
        _ready.Set();
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Native.WM_HOTKEY)
            {
                if (_ids.TryGetValue((int)msg.wParam, out var name)) Fire(name);
            }
            else if (msg.message == Native.WM_APP)
            {
                while (_ops.TryDequeue(out var op)) op.Done.TrySetResult(Apply(op.Bindings));
            }
        }
        foreach (var id in _ids.Keys) Native.UnregisterHotKey(IntPtr.Zero, id);
        _taps.Clear();
        UpdateHook();
    }

    private void Fire(string name)
    {
        try { _onHotkey(name); } catch { }
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    // ------------------------------------------------------------------ 单独按修饰键
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? name);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint TapMaxMs = 800;

    private readonly LowLevelKeyboardProc _hookProc; // 必须持有引用，否则会被 GC 回收
    private IntPtr _hook;
    private string? _candidate; // 当前按住、还没被其他键打断的修饰键
    private uint _candidateTime;

    private void UpdateHook()
    {
        if (_taps.Count > 0 && _hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Error(T("安装键盘钩子失败（{0}），单独修饰键的快捷键不可用", Marshal.GetLastWin32Error()));
        }
        else if (_taps.Count == 0 && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _candidate = null;
    }

    private static string? ModifierOf(uint vk) => vk switch
    {
        0x12 or 0xA4 or 0xA5 => "alt",
        0x11 or 0xA2 or 0xA3 => "ctrl",
        0x10 or 0xA0 or 0xA1 => "shift",
        0x5B or 0x5C => "win",
        _ => null,
    };

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) == 0) // 忽略模拟按键（比如按键说话）
            {
                bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
                var mod = ModifierOf(k.vkCode);
                if (down)
                {
                    if (mod == null) _candidate = null;              // 按了别的键：这是组合键
                    else if (_candidate == null) { _candidate = mod; _candidateTime = k.time; }
                    else if (_candidate != mod) _candidate = null;   // 同时按了两个修饰键
                    // 同一个键的自动重复不影响
                }
                else if (mod != null && mod == _candidate)
                {
                    _candidate = null;
                    if (k.time - _candidateTime <= TapMaxMs && _taps.TryGetValue(mod, out var name)) Fire(name);
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam); // 不拦截，按键照常传给游戏
    }
}
