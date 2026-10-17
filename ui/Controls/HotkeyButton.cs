using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ViaSonic.Controls;

/// <summary>
/// 点击后按下按键录入：组合键、单个键，或单独按一下 Alt / Ctrl / Shift。Esc 取消，Backspace 清除。
/// 录入期间暂停全局快捷键，否则已注册的组合会被系统吞掉。
/// SingleKey（按键说话用）：只录一个键，不组合；还能在按钮上按鼠标中键 / 侧键录成 mouse3 / mouse4 / mouse5。
/// </summary>
public sealed class HotkeyButton : Button
{
    private bool _capturing;
    private string _value = "";
    private readonly HashSet<string> _modsSeen = []; // 这次录入中按过的修饰键
    private bool _mainPressed;
    private readonly TextBlock _text = new();

    public event Action<string>? HotkeyChanged;

    public bool SingleKey { get; set; }

    public HotkeyButton()
    {
        MinWidth = 150;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(14, 6, 14, 6);
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        panel.Children.Add(_text);
        Content = panel;
        Click += (_, _) => Start();
        PreviewKeyDown += OnKey;
        PreviewKeyUp += OnKeyUp;
        LostFocus += (_, _) => Finish(null);
        // Button 自己会处理指针事件，要收到已处理的事件才能拿到中键和侧键
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointer), true);
        Show();
    }

    public string Value
    {
        get => _value;
        set { _value = value ?? ""; Show(); }
    }

    private void Show(string? text = null) => _text.Text = text ?? HotkeyText.Display(_value);

    private void Start()
    {
        if (_capturing) return;
        _capturing = true;
        _modsSeen.Clear();
        _mainPressed = false;
        App.Current.SuspendHotkeys(true);
        Show(T("按下按键"));
        Focus(FocusState.Programmatic);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        int vk = (int)e.Key;
        if (SingleKey)
        {
            if (vk == 0x1B) Finish(null);
            else if (vk == 0x08) Finish("");
            else if (HotkeyText.VkName.TryGetValue(vk, out var key)) Finish(key);
            else Show(T("不支持该键"));
            return;
        }
        var mods = HotkeyText.PressedModifiers();
        if (HotkeyText.ModifierVks.Contains(vk))
        {
            foreach (var m in mods) _modsSeen.Add(m);
            Show(string.Join(" + ", mods.Select(HotkeyText.Display)) + " + …");
            return;
        }
        _mainPressed = true;
        if (mods.Count == 0 && vk == 0x1B) { Finish(null); return; }
        if (mods.Count == 0 && vk == 0x08) { Finish(""); return; }
        if (!HotkeyText.VkName.TryGetValue(vk, out var name)) { Show(T("不支持该键")); return; }
        try
        {
            Finish(HotkeyText.Parse(string.Join("+", mods.Append(name))).Normalized);
        }
        catch (HotkeyException ex)
        {
            Show(ex.Message);
        }
    }

    private void OnPointer(object sender, PointerRoutedEventArgs e)
    {
        if (!_capturing || !SingleKey) return;
        var p = e.GetCurrentPoint(this).Properties;
        var name = p.IsMiddleButtonPressed ? "mouse3" : p.IsXButton1Pressed ? "mouse4" : p.IsXButton2Pressed ? "mouse5" : null;
        if (name == null) return;
        e.Handled = true;
        Finish(name);
    }

    /// <summary>修饰键全部松开、期间没按过别的键：录为「单独按一下这个修饰键」。</summary>
    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;
        if (SingleKey) return;
        if (_mainPressed || _modsSeen.Count == 0 || !HotkeyText.ModifierVks.Contains((int)e.Key)) return;
        if (HotkeyText.PressedModifiers().Count > 0) return; // 还有修饰键没松开
        var seen = _modsSeen.ToList();
        _modsSeen.Clear();
        try
        {
            Finish(HotkeyText.Parse(string.Join("+", seen)).Normalized);
        }
        catch (HotkeyException ex)
        {
            Show(ex.Message);
        }
    }

    private void Finish(string? value)
    {
        if (!_capturing) return;
        _capturing = false;
        if (value != null && value != _value)
        {
            _value = value;
            HotkeyChanged?.Invoke(value);
        }
        Show();
        App.Current.SuspendHotkeys(false);
    }
}
