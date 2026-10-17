using System.Text.Json.Nodes;
using ViaSonic.Audio;
using Windows.Media.SpeechSynthesis;

namespace ViaSonic.Engines;

/// <summary>Windows 自带语音（离线）：慧慧、康康、瑶瑶等，在系统「语音」设置里可以添加更多。</summary>
public sealed class SystemTts : ITtsEngine
{
    public string Key => "sapi";
    public string Label => T("系统语音");
    public bool Ready => true;
    public bool Loading => false;
    public string Error => "";
    public string ErrorKind => "";

    public string CacheKey(JsonObject c) => $"sapi|{c["sapi_voice"]}|{c["sapi_rate"]}";
    public void Load(JsonObject cfg) { }

    public static List<(string Id, string Name)> ListVoices()
    {
        try
        {
            return SpeechSynthesizer.AllVoices.Select(v => (v.Id, $"{v.DisplayName}（{v.Language}）")).ToList();
        }
        catch (Exception e)
        {
            Log.Error(T("读取系统语音失败"), e);
            return [];
        }
    }

    public async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        using var synth = new SpeechSynthesizer();
        var want = c["sapi_voice"]?.GetValue<string>() ?? "";
        var voices = SpeechSynthesizer.AllVoices;
        var voice = voices.FirstOrDefault(v => v.Id == want)
                    ?? voices.FirstOrDefault(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
        if (voice != null) synth.Voice = voice;
        // 界面上是 -10 ~ 10，换算成 0.5x ~ 2.5x
        double r = App.ToDouble(c["sapi_rate"]);
        synth.Options.SpeakingRate = r >= 0 ? 1 + r * 0.15 : 1 + r * 0.05;
        try
        {
            using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(token);
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms, token);
            return AudioDecode.FromWave(ms.ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            throw new TtsException(T("系统语音失败：{0}", e.Message));
        }
    }
}
