using System.Text.Json.Nodes;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

public class TtsException(string message, string kind = "") : Exception(message)
{
    /// <summary>gsv_missing（没装 GPT-SoVITS）/ model_missing（模型文件不全）/ 空。</summary>
    public string Kind { get; } = kind;
}

public interface ITtsEngine
{
    string Key { get; }
    string Label { get; }
    bool Ready { get; }
    bool Loading { get; }
    string Error { get; }
    string ErrorKind { get; }
    /// <summary>参数变化时缓存失效。</summary>
    string CacheKey(JsonObject cfg);
    void Load(JsonObject cfg);
    Task<Clip> SynthAsync(string text, JsonObject cfg, CancellationToken token);
}
