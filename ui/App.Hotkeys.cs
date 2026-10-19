namespace ViaSonic;

/// <summary>全局快捷键：注册、冲突说明、按下后做什么。</summary>
public partial class App
{
    public Dictionary<string, HotkeyError> HotkeyErrors { get; private set; } = [];
    public event Action? HotkeyErrorsChanged;
    private HotkeyManager? _hotkeys;
    private int _suspend;

    public static readonly Dictionary<string, string> HotkeyLabels = new()
    {
        ["input"] = "打开/关闭输入框", // 翻译键
        ["focus"] = "聚焦输入框", // 翻译键
        ["stop"] = "停止", // 翻译键
        ["repeat"] = "重复上一句", // 翻译键
        ["ptt"] = "打开/关闭按键说话", // 翻译键
        ["playlist"] = "播放/暂停音乐", // 翻译键
        ["next"] = "下一首", // 翻译键
    };

    public void SuspendHotkeys(bool on)
    {
        _suspend += on ? 1 : -1;
        ApplyHotkeys();
    }

    public void ApplyHotkeys()
    {
        if (_hotkeys == null) return;
        if (_suspend > 0)
        {
            _hotkeys.SetBindings([]);
            return;
        }
        var hk = Sub("hotkeys");
        // 「输入框」「聚焦输入框」不受「暂停快捷键」影响
        var b = new Dictionary<string, string>
        {
            ["input"] = hk.Str("input"),
            ["focus"] = hk.Str("focus"),
        };
        if (!Bool("hotkeys_paused"))
        {
            b["stop"] = hk.Str("stop");
            b["repeat"] = hk.Str("repeat");
            b["ptt"] = hk.Str("ptt");
            b["playlist"] = hk.Str("playlist");
            b["next"] = hk.Str("next");
            foreach (var p in Phrases)
                b["phrase:" + p.Str("id")] = p.Str("hotkey");
        }
        HotkeyErrors = _hotkeys.SetBindings(b);
        HotkeyErrorsChanged?.Invoke();
    }

    public string DescribeBinding(string name)
    {
        if (HotkeyLabels.TryGetValue(name, out var l)) return T(l);
        if (name.StartsWith("phrase:"))
        {
            var id = name[7..];
            var p = Phrases.FirstOrDefault(x => x.Str("id") == id);
            var audio = p.Str("audio");
            var t = audio.Length > 0 ? p.Str("name") : p.Str("text");
            if (t.Length == 0) t = Path.GetFileName(audio) is { Length: > 0 } f ? f : T("未命名");
            return T("常用语「{0}」", t.Length > 12 ? t[..12] + "…" : t);
        }
        return name;
    }

    public string DescribeError(string name, HotkeyError e) =>
        T("{0}：{1}", DescribeBinding(name), e.ConflictWith != null ? T("与「{0}」重复", DescribeBinding(e.ConflictWith)) : e.Message);

    private void OnHotkeyThread(string name)
    {
        var fg = Native.GetForegroundWindow(); // 记下游戏窗口，发送后把焦点还回去
        Post(() => OnHotkey(name, fg));
    }

    private void OnHotkey(string name, IntPtr prev)
    {
        switch (name)
        {
            case "input": Overlay?.Toggle(prev); break;
            case "focus": Overlay?.FocusIfVisible(prev); break;
            case "stop": Speaker.Stop(); break;
            case "repeat": Speaker.Repeat(); break;
            case "ptt": Set("ptt_enabled", !Bool("ptt_enabled")); PttToggled?.Invoke(); break;
            case "playlist": Playlist.Toggle(); break;
            case "next": Playlist.Next(); break;
            default:
                if (name.StartsWith("phrase:"))
                {
                    var id = name[7..];
                    var p = Phrases.FirstOrDefault(x => x.Str("id") == id);
                    if (p != null) PlayPhrase(p);
                }
                break;
        }
    }
}
