using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

/// <summary>Azure 语音（官方）：Key + 区域，音色列表从接口获取，支持说话风格。</summary>
public sealed class AzureTts(Func<JsonObject> cfg) : OnlineEngine(cfg)
{
    public sealed record AzureVoice(string Id, string Locale, string LocaleName, string LocalName, string DisplayName, string Gender, string[] Styles)
    {
        public string Name => Lang.Current == "en" || LocalName.Length == 0 ? DisplayName : LocalName;
    }

    private static (string Key, List<AzureVoice> List)? _voices;

    public override string Key => "azure";
    public override string Label => T("Azure 语音");

    public override string Missing(JsonObject c) =>
        base.Missing(c) is { Length: > 0 } m ? m : S(c, "azure_region").Length == 0 ? T("请先填写区域") : "";

    public override string CacheKey(JsonObject c) => $"azure|{c["azure_region"]}|{c["azure_voice"]}|{c["azure_style"]}|{c["azure_rate"]}";

    private static string Host(JsonObject c) => $"https://{S(c, "azure_region").Trim().ToLowerInvariant()}.tts.speech.microsoft.com";

    /// <summary>当前 Key 和区域下的全部音色，取过一次就缓存。</summary>
    public async Task<List<AzureVoice>> VoicesAsync(JsonObject c, bool refresh = false)
    {
        var cacheKey = $"{ApiKey(c)}|{S(c, "azure_region")}";
        if (!refresh && _voices is { } hit && hit.Key == cacheKey) return hit.List;
        using var req = new HttpRequestMessage(HttpMethod.Get, Host(c) + "/cognitiveservices/voices/list");
        req.Headers.Add("Ocp-Apim-Subscription-Key", ApiKey(c));
        using var r = await SendAsync(req, CancellationToken.None);
        var list = JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsArray().OfType<JsonObject>()
            .Select(v => new AzureVoice(
                v.Str("ShortName"),
                v.Str("Locale"),
                v.Str("LocaleName"),
                v.Str("LocalName"),
                v.Str("DisplayName"),
                v.Str("Gender"),
                v["StyleList"]?.AsArray().Select(s => s?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? []))
            .Where(v => v.Id.Length > 0)
            .ToList();
        _voices = (cacheKey, list);
        return list;
    }

    public override async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        CheckReady(c);
        var voice = S(c, "azure_voice", "zh-CN-XiaoxiaoNeural");
        var locale = voice.Length > 5 && voice[2] == '-' ? voice[..voice.IndexOf('-', 3)] : "zh-CN";
        var style = S(c, "azure_style");
        int rate = (int)c.Num("azure_rate");
        var body = new StringBuilder()
            .Append($"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='{locale}'>")
            .Append($"<voice name='{SecurityElement.Escape(voice)}'>");
        if (style.Length > 0) body.Append($"<mstts:express-as style='{SecurityElement.Escape(style)}'>");
        body.Append($"<prosody rate='{rate:+0;-0;+0}%'>{SecurityElement.Escape(text)}</prosody>");
        if (style.Length > 0) body.Append("</mstts:express-as>");
        body.Append("</voice></speak>");

        using var req = new HttpRequestMessage(HttpMethod.Post, Host(c) + "/cognitiveservices/v1")
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8, "application/ssml+xml"),
        };
        req.Headers.Add("Ocp-Apim-Subscription-Key", ApiKey(c));
        req.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        req.Headers.Add("User-Agent", "ViaSonic");
        using var r = await SendAsync(req, token);
        return Decode(await r.Content.ReadAsByteArrayAsync(token));
    }
}
