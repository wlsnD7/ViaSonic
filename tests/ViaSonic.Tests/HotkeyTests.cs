using ViaSonic.Audio;

namespace ViaSonic.Tests;

public class HotkeyTextTests
{
    [Theory]
    [InlineData("ctrl+alt+t", "ctrl+alt+t")]
    [InlineData("Alt + Ctrl + T", "ctrl+alt+t")]       // 修饰键按固定顺序
    [InlineData("control+shift+f5", "ctrl+shift+f5")]
    [InlineData("meta+num0", "win+num0")]
    [InlineData("alt", "alt")]                         // 单独按一下修饰键
    public void Normalizes(string input, string expected) => Assert.Equal(expected, HotkeyText.Parse(input).Normalized);

    [Fact]
    public void ReturnsWin32Codes()
    {
        var (mods, vk, _) = HotkeyText.Parse("ctrl+alt+t");
        Assert.Equal(Native.MOD_CONTROL | Native.MOD_ALT, mods);
        Assert.Equal('T', vk);
        Assert.Equal((0, 0), (HotkeyText.Parse("shift").Mods, HotkeyText.Parse("shift").Vk));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+a+b")]     // 两个主键
    [InlineData("ctrl+nokey")]
    [InlineData("ctrl+alt")]     // 两个修饰键、没有主键
    [InlineData("win")]          // 会打开开始菜单
    public void RejectsBadInput(string input) => Assert.Throws<HotkeyException>(() => HotkeyText.Parse(input));

    [Fact]
    public void DisplaysReadably()
    {
        using var _ = new LangScope("zh");
        Assert.Equal("Ctrl + Alt + T", HotkeyText.Display("ctrl+alt+t"));
        Assert.Equal("Shift + Num5", HotkeyText.Display("shift+num5"));
        Assert.Equal("F12", HotkeyText.Display("f12"));
        Assert.Equal("未设置", HotkeyText.Display(""));
    }
}

public class PttTests
{
    [Theory]
    [InlineData("V", "v")]
    [InlineData(" mouse4 ", "mouse4")]
    [InlineData("capslock", "capslock")]
    [InlineData("", "")]
    public void ParsesKeyNames(string input, string expected) => Assert.Equal(expected, Ptt.Parse(input));

    [Fact]
    public void RejectsUnknownKey() => Assert.Throws<HotkeyException>(() => Ptt.Parse("mouse9"));

    private sealed class Keyboard
    {
        public readonly List<string> Log = [];
        public void Press(string key, bool down) { lock (Log) Log.Add((down ? "+" : "-") + key); }
    }

    private static PttHold Hold(Keyboard kb, Func<string> key) => new(key, () => 0, kb.Press, releaseMs: 0);

    [Fact]
    public async Task OverlappingSoundsPressOnce()
    {
        var kb = new Keyboard();
        var ptt = Hold(kb, () => "v");
        Assert.True(ptt.Down(CancellationToken.None));
        Assert.True(ptt.Down(CancellationToken.None));
        await ptt.Up();
        Assert.Equal(["+v"], kb.Log);
        await ptt.Up();
        Assert.Equal(["+v", "-v"], kb.Log);
    }

    [Fact]
    public async Task NoKeyNoPress()
    {
        var kb = new Keyboard();
        var ptt = Hold(kb, () => "");
        Assert.True(ptt.Down(CancellationToken.None));
        await ptt.Up();
        Assert.Empty(kb.Log);
    }

    [Fact]
    public void CancelledWhileWaiting()
    {
        var kb = new Keyboard();
        var ptt = new PttHold(() => "v", () => 5000, kb.Press, releaseMs: 0);
        using var cts = new CancellationTokenSource(50);
        var t0 = DateTime.UtcNow;
        Assert.False(ptt.Down(cts.Token));
        Assert.True(DateTime.UtcNow - t0 < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task RefreshFollowsSettingsWhileHeld()
    {
        var kb = new Keyboard();
        var key = "v";
        var ptt = Hold(kb, () => key);
        ptt.Refresh(); // 没在播放时什么都不做
        Assert.Empty(kb.Log);
        ptt.Down(CancellationToken.None);
        key = "b";
        ptt.Refresh();
        key = "";
        ptt.Refresh();
        Assert.Equal(["+v", "-v", "+b", "-b"], kb.Log);
        await ptt.Up();
        Assert.Equal(4, kb.Log.Count); // 已经松开了，不再松一次
    }

    [Fact]
    public void ReleaseOnExit()
    {
        var kb = new Keyboard();
        var ptt = Hold(kb, () => "v");
        ptt.Release();
        Assert.Empty(kb.Log);
        ptt.Down(CancellationToken.None);
        ptt.Release();
        Assert.Equal(["+v", "-v"], kb.Log);
    }
}
