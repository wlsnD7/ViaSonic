namespace ViaSonic;

public class HotkeyException(string message) : Exception(message);

/// <summary>快捷键字符串（如 "ctrl+alt+t"）与 Win32 修饰键/虚拟键码互转，格式与 Python 后台一致。</summary>
public static class HotkeyText
{
    public static readonly Dictionary<string, int> Vk = BuildVk();
    public static readonly Dictionary<int, string> VkName = Vk.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
    public static readonly HashSet<int> ModifierVks = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    private static readonly (string Name, int Flag)[] Mods =
        [("ctrl", Native.MOD_CONTROL), ("alt", Native.MOD_ALT), ("shift", Native.MOD_SHIFT), ("win", Native.MOD_WIN)];

    private static Dictionary<string, int> BuildVk()
    {
        var d = new Dictionary<string, int>();
        for (char c = 'A'; c <= 'Z'; c++) d[char.ToLower(c).ToString()] = c;
        for (int i = 0; i <= 9; i++) { d[i.ToString()] = 0x30 + i; d["num" + i] = 0x60 + i; }
        for (int i = 1; i <= 24; i++) d["f" + i] = 0x6F + i;
        foreach (var (k, v) in new (string, int)[] {
            ("space", 0x20), ("enter", 0x0D), ("tab", 0x09), ("esc", 0x1B), ("backspace", 0x08),
            ("insert", 0x2D), ("delete", 0x2E), ("home", 0x24), ("end", 0x23), ("pageup", 0x21), ("pagedown", 0x22),
            ("left", 0x25), ("up", 0x26), ("right", 0x27), ("down", 0x28),
            ("pause", 0x13), ("scrolllock", 0x91), ("capslock", 0x14),
            ("`", 0xC0), ("-", 0xBD), ("=", 0xBB), ("[", 0xDB), ("]", 0xDD), ("\\", 0xDC),
            (";", 0xBA), ("'", 0xDE), (",", 0xBC), (".", 0xBE), ("/", 0xBF),
            ("num*", 0x6A), ("num+", 0x6B), ("num-", 0x6D), ("num.", 0x6E), ("num/", 0x6F) })
            d[k] = v;
        return d;
    }

    public static (int Mods, int Vk, string Normalized) Parse(string? text)
    {
        var parts = (text ?? "").Replace(" ", "").ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new HotkeyException(T("快捷键为空"));
        int mods = 0;
        string? key = null;
        foreach (var raw in parts)
        {
            var p = raw switch { "control" => "ctrl", "menu" => "alt", "meta" or "super" => "win", _ => raw };
            var m = Array.Find(Mods, x => x.Name == p);
            if (m.Name != null) mods |= m.Flag;
            else if (Vk.ContainsKey(p))
            {
                if (key != null) throw new HotkeyException(T("只能有一个主键"));
                key = p;
            }
            else throw new HotkeyException(T("无法识别：{0}", p));
        }
        if (key == null)
        {
            // 单独按一下修饰键：Vk 为 0，由 HotkeyManager 的键盘钩子处理
            var only = Mods.Where(x => (mods & x.Flag) != 0).Select(x => x.Name).ToList();
            if (only.Count > 1) throw new HotkeyException(T("只能单独用一个修饰键"));
            if (only[0] == "win") throw new HotkeyException(T("Win 键单独按会打开开始菜单，不能使用"));
            return (0, 0, only[0]);
        }
        var names = Mods.Where(x => (mods & x.Flag) != 0).Select(x => x.Name).Append(key);
        return (mods, Vk[key], string.Join("+", names));
    }

    public static List<string> PressedModifiers()
    {
        var list = new List<string>();
        if (Native.IsDown(0x11)) list.Add("ctrl");
        if (Native.IsDown(0x12)) list.Add("alt");
        if (Native.IsDown(0x10)) list.Add("shift");
        if (Native.IsDown(0x5B) || Native.IsDown(0x5C)) list.Add("win");
        return list;
    }

    public static string Display(string? text) =>
        string.IsNullOrEmpty(text) ? T("未设置") : string.Join(" + ", text.Split('+').Select(Pretty));

    private static string Pretty(string p) => p switch
    {
        "ctrl" => "Ctrl", "alt" => "Alt", "shift" => "Shift", "win" => "Win",
        "space" => "Space", "enter" => "Enter", "esc" => "Esc",
        _ when p.StartsWith("num") && p.Length > 3 => "Num" + p[3..],
        _ => p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..],
    };
}

