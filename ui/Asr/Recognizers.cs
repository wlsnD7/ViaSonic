using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ViaSonic.Engines;

namespace ViaSonic.Asr;

/// <summary>语音识别：16kHz 单声道的一段话 → 文字。</summary>
public interface IRecognizer
{
    string Key { get; }
    string Label { get; }
    /// <summary>还缺什么设置（语音输入卡片的状态显示）；空＝可以识别。</summary>
    string Missing(JsonObject c);
    Task<string> RecognizeAsync(float[] pcm, JsonObject c, CancellationToken token);
}

/// <summary>识别出错，Message 是给用户看的说明。</summary>
public class AsrException(string message) : Exception(message);

public static class Pcm
{
    public const int Rate = 16000;

    /// <summary>16kHz 单声道 16 位 WAV，上传给在线识别。</summary>
    public static byte[] Wav(float[] pcm)
    {
        using var ms = new MemoryStream(44 + pcm.Length * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcm.Length * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(pcm.Length * 2);
        foreach (var s in pcm) w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767));
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>在线识别的共同部分：HTTP 请求、错误提示。Key 和朗读引擎共用同一份。</summary>
public abstract class OnlineAsr : IRecognizer
{
    public abstract string Key { get; }
    public abstract string Label { get; }
    public abstract string Missing(JsonObject c);
    public abstract Task<string> RecognizeAsync(float[] pcm, JsonObject c, CancellationToken token);

    protected static string S(JsonObject c, string key, string def = "")
    {
        var v = c.Str(key).Trim();
        return v.Length > 0 ? v : def;
    }

    protected async Task<string> SendAsync(HttpRequestMessage req, CancellationToken token)
    {
        HttpResponseMessage r;
        try
        {
            r = await OnlineEngine.Http.SendAsync(req, token);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            throw new AsrException(T("{0} 请求失败：{1}", Label, T("连接超时")));
        }
        catch (HttpRequestException e)
        {
            throw new AsrException(T("{0} 请求失败：{1}", Label, e.Message));
        }
        using (r)
        {
            var body = await r.Content.ReadAsStringAsync(token);
            if (r.IsSuccessStatusCode) return body;
            if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new AsrException(T("API Key 无效或已过期"));
            var msg = OnlineEngine.ErrorText(body);
            throw new AsrException(T("{0} 请求失败：{1}", Label, msg.Length > 0 ? msg : $"HTTP {(int)r.StatusCode}"));
        }
    }

    protected JsonNode ParseJson(string body)
    {
        try
        {
            return JsonNode.Parse(body) ?? throw new FormatException();
        }
        catch (Exception)
        {
            throw new AsrException(T("{0} 请求失败：{1}", Label, OnlineEngine.ErrorText(body)));
        }
    }
}

/// <summary>Azure 语音：短语音 REST 接口（一段最长 60 秒）。</summary>
public sealed class AzureAsr : OnlineAsr
{
    /// <summary>识别语言：(Azure 语言代码, 显示名)。</summary>
    public static readonly (string Id, string Name)[] Langs =
        [("zh-CN", "中文"), ("zh-HK", "粤语"), ("en-US", "英文"), ("ja-JP", "日文"), ("ko-KR", "韩文")]; // 翻译键

    public override string Key => "azure";
    public override string Label => T("Azure 语音");

    /// <summary>没选过语言时跟界面语言一致。</summary>
    public static string Lang(JsonObject c) => S(c, "stt_azure_lang", ViaSonic.Lang.Current == "en" ? "en-US" : "zh-CN");

    public override string Missing(JsonObject c) =>
        Secrets.Get(c, "azure_key").Length == 0 ? T("未填写 API Key") : S(c, "azure_region").Length == 0 ? T("请先填写区域") : "";

    public override async Task<string> RecognizeAsync(float[] pcm, JsonObject c, CancellationToken token)
    {
        var region = S(c, "azure_region").ToLowerInvariant();
        var url = $"https://{region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language={Lang(c)}&format=simple";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Pcm.Wav(pcm)) };
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "audio/wav; codecs=audio/pcm; samplerate=16000");
        req.Headers.Add("Ocp-Apim-Subscription-Key", Secrets.Get(c, "azure_key"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return Parse(await SendAsync(req, token));
    }

    /// <summary>{"RecognitionStatus":"Success","DisplayText":"…"}；没听到话是 NoMatch / InitialSilenceTimeout。</summary>
    internal static string Parse(string body)
    {
        var j = JsonNode.Parse(body);
        return j.Str("RecognitionStatus") == "Success" ? j.Str("DisplayText") : "";
    }
}

/// <summary>阿里云百炼：Qwen3-ASR-Flash，OpenAI 兼容的对话接口，音频用 Base64 放进消息里。</summary>
public sealed class BailianAsr : OnlineAsr
{
    public override string Key => "bailian";
    public override string Label => T("阿里云百炼");

    public override string Missing(JsonObject c) => Secrets.Get(c, "bailian_key").Length == 0 ? T("未填写 API Key") : "";

    public override async Task<string> RecognizeAsync(float[] pcm, JsonObject c, CancellationToken token)
    {
        var host = S(c, "bailian_region") == "intl" ? "dashscope-intl.aliyuncs.com" : "dashscope.aliyuncs.com";
        var audio = new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = new JsonObject { ["data"] = "data:audio/wav;base64," + Convert.ToBase64String(Pcm.Wav(pcm)) },
        };
        var body = new JsonObject
        {
            ["model"] = "qwen3-asr-flash",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(audio) }),
            ["stream"] = false,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/compatible-mode/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secrets.Get(c, "bailian_key"));
        return Parse(ParseJson(await SendAsync(req, token)));
    }

    internal static string Parse(JsonNode j) =>
        j["choices"] is JsonArray { Count: > 0 } choices ? choices[0]?["message"].Str("content") ?? "" : "";
}

/// <summary>自定义接口：OpenAI 格式的语音转文字（/v1/audio/transcriptions，multipart 上传文件）。</summary>
public sealed class CustomAsr : OnlineAsr
{
    public override string Key => "custom";
    public override string Label => T("自定义接口");

    public override string Missing(JsonObject c) => S(c, "stt_custom_url").Length == 0 ? T("还没有填写地址") : "";

    public override async Task<string> RecognizeAsync(float[] pcm, JsonObject c, CancellationToken token)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Pcm.Wav(pcm));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        if (S(c, "stt_custom_model") is { Length: > 0 } model) form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");
        using var req = new HttpRequestMessage(HttpMethod.Post, S(c, "stt_custom_url")) { Content = form };
        if (Secrets.Get(c, "stt_custom_key") is { Length: > 0 } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return Parse(await SendAsync(req, token));
    }

    /// <summary>{"text":"…"}；不是 JSON 时把整个返回当文字。</summary>
    internal static string Parse(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject o) return o.Str("text");
        }
        catch { }
        return body.Trim();
    }
}
