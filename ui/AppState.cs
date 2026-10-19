namespace ViaSonic;

/// <summary>主窗口、主页、输入框显示的状态。每次变化整个换掉，界面线程读。</summary>
/// <param name="Busy">「合成中」「播放中」等（翻译键），空＝空闲。</param>
/// <param name="Error">最近一次朗读出错的提示；ErrorSeq 每出一次错加一，用来判断要不要弹出。</param>
public sealed record AppState(string Busy, string Error, int ErrorSeq, IReadOnlyList<EngineState> Engines)
{
    public static readonly AppState Empty = new("", "", 0, []);

    public EngineState? Engine(string key) => Engines.FirstOrDefault(e => e.Key == key);
}

/// <param name="ErrorKind">gsv_missing / model_missing / 空，见 TtsException.Kind。</param>
/// <param name="Missing">联网服务还缺什么设置（没填 Key 等），空＝可以合成。</param>
public sealed record EngineState(string Key, string Label, bool Ready, bool Loading, string Error, string ErrorKind, string Missing);
