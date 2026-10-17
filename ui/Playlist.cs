using System.Text.Json.Nodes;

namespace ViaSonic;

/// <summary>
/// 播放列表：一次放一首，放完按模式接下一首（列表循环 / 单曲循环 / 随机播放）。
/// 走音效的播放通路：进虚拟麦克风和监听，音量是「音频音量」，按键说话跟着开关走，和音效、语音叠加。
/// 暂停、切歌时短暂淡出淡入；打开「过渡」后，上一首结尾和下一首开头交叉重叠。
/// 为了能重叠，两首歌轮流用 A、B 两个音效 id。
/// </summary>
public sealed class Playlist
{
    private static readonly string[] Slots = ["playlist", "playlist2"];
    private const double ShortFade = 0.15; // 暂停、切歌的淡入淡出（秒）

    private readonly App _app;
    private readonly Random _rng = new();
    private readonly Timer _watch;
    private int _slot;          // 当前这首用的 id
    private int _gen;           // 每开始一首加一，旧的那首结束回调来晚了就忽略
    private int _crossedGen;    // 这一首已经开始交叉过渡了
    private double _pausedAt;   // 暂停时的位置（秒）
    private double _length;     // 当前这首的总长（秒），暂停时也能显示进度
    private string? _shuffleNext; // 随机模式下已经选好（并提前解码）的下一首的 id

    public int Index { get; private set; } = -1;
    public bool Playing { get; private set; }
    public bool Paused { get; private set; }

    /// <summary>状态变化（任意线程）。</summary>
    public event Action? Changed;

    public Playlist(App app)
    {
        _app = app;
        _watch = new Timer(_ => App.Guard(Watch), null, 100, 100);
    }

    public JsonArray Items => _app.Config["playlist"]!.AsArray();
    public string Mode => _app.Str("playlist_mode") is "one" or "shuffle" ? _app.Str("playlist_mode") : "all";

    /// <summary>播放速度（变速不变调）。不存进配置，每次启动都是 1。</summary>
    public double Speed { get; set; } = 1;

    /// <summary>交叉过渡的秒数，0＝关。</summary>
    public double Crossfade => Math.Clamp(_app.Num("playlist_crossfade"), 0, 10);

    private string SoundId => Slots[_slot];

    public string? CurrentPath => Index >= 0 && Index < Items.Count ? Items[Index]?["path"]?.GetValue<string>() : null;
    public string? CurrentId => Index >= 0 && Index < Items.Count ? Items[Index]?["id"]?.GetValue<string>() : null;

    /// <summary>播放第 index 首。正在放的那首淡出；crossfade：交叉过渡时新歌的淡入秒数。</summary>
    public void Play(int index, double startAt = 0, double crossfade = 0)
    {
        if (Items.Count == 0) return;
        // 交叉过渡时旧歌按自己的结尾淡出放完；手动切歌时短暂淡出
        if (crossfade <= 0) _app.Speaker.Player.FadeStopSound(SoundId, ShortFade);
        _slot = 1 - _slot; // 新歌用另一个 id，和旧的重叠淡出
        Index = Math.Clamp(index, 0, Items.Count - 1);
        var path = CurrentPath;
        if (path == null) return;
        int gen = Interlocked.Increment(ref _gen);
        Playing = true;
        Paused = false;
        _length = 0;
        var fade = Crossfade;
        _app.Speaker.Player.SetStart(SoundId, startAt);
        _app.Speaker.PlaySound(SoundId, path, finished => Ended(gen, finished), cache: false,
            fadeIn: crossfade > 0 ? crossfade : ShortFade, fadeTail: fade, speed: () => Speed);
        _shuffleNext = null;
        PrefetchNext();
        Changed?.Invoke();
    }

    /// <summary>提前解码接下来要放的那首（列表、模式变了也调用）。</summary>
    public void PrefetchNext()
    {
        int next = Playing || Paused ? NextIndex(manual: false) : -1;
        _app.Speaker.Prefetch(next >= 0 && next < Items.Count ? Items[next]?["path"]?.GetValue<string>() : null);
    }

    /// <summary>播放 / 暂停（快捷键和播放按钮）。</summary>
    public void Toggle()
    {
        if (Playing) Pause();
        else if (Paused) Play(Index, _pausedAt);
        else Play(Index >= 0 ? Index : 0);
    }

    public void Pause()
    {
        if (!Playing) return;
        var p = _app.Speaker.Player.Progress(SoundId);
        _pausedAt = p?.Pos ?? 0;
        if (p is { } pp) _length = pp.Len;
        Interlocked.Increment(ref _gen); // 停下来不算播完
        Playing = false;
        Paused = true;
        _app.Speaker.Player.FadeStopSound(SoundId, ShortFade);
        Changed?.Invoke();
    }

    /// <summary>下一首：随机模式随机挑，其他模式按顺序（单曲循环时手动下一首也换歌）。</summary>
    public void Next() => Play(NextIndex(manual: true));

    public void Previous()
    {
        if (Items.Count == 0) return;
        Play(Index <= 0 ? Items.Count - 1 : Index - 1);
    }

    public void Seek(double seconds)
    {
        if (Playing) _app.Speaker.Player.Seek(SoundId, seconds);
        else if (Paused) _pausedAt = seconds;
        Changed?.Invoke();
    }

    /// <summary>当前位置和总长（秒）；没有在放也没暂停时为 null。</summary>
    public (double Pos, double Len)? Progress()
    {
        if (Playing && _app.Speaker.Player.Progress(SoundId) is { } p)
        {
            _length = p.Len;
            return p;
        }
        return Paused && _length > 0 ? (_pausedAt, _length) : null;
    }

    private int NextIndex(bool manual)
    {
        int n = Items.Count;
        if (n == 0) return -1;
        if (Mode == "one" && !manual) return Math.Max(Index, 0);
        if (Mode == "shuffle" && n > 1)
        {
            // 同一首歌放着时选好的下一首不变，这样提前解码的就是真正要放的那首
            for (int k = 0; k < n; k++)
                if (k != Index && _shuffleNext != null && Items[k]?["id"]?.GetValue<string>() == _shuffleNext) return k;
            int i;
            do i = _rng.Next(n); while (i == Index);
            _shuffleNext = Items[i]?["id"]?.GetValue<string>();
            return i;
        }
        return (Index + 1) % n;
    }

    /// <summary>后退 / 前进 seconds 秒（按原曲时间）。</summary>
    public void Skip(double seconds)
    {
        if (Progress() is not { } p) return;
        Seek(Math.Clamp(p.Pos + seconds, 0, Math.Max(0, p.Len - 0.1)));
    }

    /// <summary>交叉过渡：这首还剩不到「过渡」秒数（按实际播放时间）时，提前开始下一首。歌太短（不到两倍过渡时长）就不交叉。</summary>
    private void Watch()
    {
        var fade = Crossfade;
        int gen = Volatile.Read(ref _gen);
        if (!Playing || fade <= 0 || _crossedGen == gen || Items.Count == 0) return;
        double speed = Speed;
        if (_app.Speaker.Player.Progress(SoundId) is not { } p || p.Len / speed < fade * 2 || (p.Len - p.Pos) / speed > fade) return;
        _crossedGen = gen;
        _app.Post(() =>
        {
            if (gen == Volatile.Read(ref _gen) && Playing) Play(NextIndex(manual: false), crossfade: fade);
        });
    }

    private void Ended(int gen, bool finished)
    {
        if (gen != Volatile.Read(ref _gen)) return; // 已经换歌或暂停
        if (finished && Items.Count > 0)
        {
            _app.Post(() => { if (gen == Volatile.Read(ref _gen)) Play(NextIndex(manual: false)); });
            return;
        }
        // 被「停止全部」或出错停下
        Playing = false;
        Paused = false;
        PrefetchNext();
        Changed?.Invoke();
    }

    /// <summary>列表变了（删除、排序）：当前这首跟着移动；被删掉就停止。</summary>
    public void OnItemsChanged(string? currentId)
    {
        int i = currentId == null ? -1 : Items.Select((x, k) => (x?["id"]?.GetValue<string>(), k)).FirstOrDefault(t => t.Item1 == currentId, (null, -1)).Item2;
        if (i < 0 && (Playing || Paused))
        {
            Interlocked.Increment(ref _gen);
            _app.Speaker.Player.FadeStopSound(SoundId, ShortFade);
            Playing = Paused = false;
        }
        Index = i;
        PrefetchNext();
        Changed?.Invoke();
    }
}
