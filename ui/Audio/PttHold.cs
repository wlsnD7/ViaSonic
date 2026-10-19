namespace ViaSonic.Audio;

/// <summary>
/// 播放期间按住按键说话的键：几个声音叠在一起时只按一次，第一个开始时按下（等一会儿让游戏反应过来），
/// 最后一个播完稍等再松开。播放途中开关按键说话或换键立即生效。
/// </summary>
/// <param name="key">现在要按的键；按键说话关着时为空。</param>
/// <param name="delayMs">按下后等多久再出声。</param>
/// <param name="press">按下/松开（测试时换掉，平时是 Ptt.Press）。</param>
internal sealed class PttHold(Func<string> key, Func<int> delayMs, Action<string, bool>? press = null, int releaseMs = 100)
{
    private readonly object _lock = new();
    private readonly Action<string, bool> _press = press ?? Ptt.Press;
    private int _count;
    private string _key = "";

    /// <summary>一个声音开始；已经按着（别的声音在播）时不用再等。返回 false 表示等待期间被取消。</summary>
    public bool Down(CancellationToken token)
    {
        bool wait;
        lock (_lock)
        {
            wait = _count++ == 0;
            if (wait)
            {
                _key = key();
                if (_key.Length > 0) _press(_key, true);
                else wait = false;
            }
        }
        if (wait) token.WaitHandle.WaitOne(delayMs());
        return !token.IsCancellationRequested;
    }

    /// <summary>一个声音结束（每次 Down 都要对应一次 Up）。</summary>
    public Task Up() => Task.Delay(releaseMs).ContinueWith(_ =>
    {
        lock (_lock)
            if (--_count == 0 && _key.Length > 0) _press(_key, false);
    });

    /// <summary>设置变了：正按着时，关掉就松开，换了键就换着按。</summary>
    public void Refresh()
    {
        lock (_lock)
        {
            if (_count == 0) return;
            var k = key();
            if (k == _key) return;
            if (_key.Length > 0) _press(_key, false);
            _key = k;
            if (_key.Length > 0) _press(_key, true);
        }
    }

    /// <summary>退出时还按着就松开。</summary>
    public void Release()
    {
        lock (_lock) if (_count > 0 && _key.Length > 0) _press(_key, false);
    }
}
