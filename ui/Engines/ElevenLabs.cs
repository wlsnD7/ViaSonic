using System.Net;
using System.Text.Json.Nodes;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

/// <summary>ElevenLabs：音色列表是账号里的音色（官方预置的加上自己从音色库添加、复刻的）。返回 MP3。</summary>
public sealed class ElevenLabs(Func<JsonObject> cfg) : OnlineEngine(cfg)
{
    private const string Api = "https://api.elevenlabs.io";
    /// <summary>George，官方预置音色，还没取到列表时用。</summary>
    public const string DefaultVoice = "JBFqnCBsd6RMkjVDRZzb";

    private static (string Key, List<(string Id, string Name)> List)? _voices;

    public override string Key => "elevenlabs";
    public override string Label => "ElevenLabs";

    public override string CacheKey(JsonObject c) => $"elevenlabs|{c["elevenlabs_model"]}|{c["elevenlabs_voice"]}|{c["elevenlabs_speed"]}";

    private HttpRequestMessage Request(HttpMethod method, string path, JsonObject c)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Add("xi-api-key", ApiKey(c));
        return req;
    }

    public async Task<List<(string Id, string Name)>> VoicesAsync(JsonObject c, bool refresh = false)
    {
        var cacheKey = ApiKey(c);
        if (!refresh && _voices is { } hit && hit.Key == cacheKey) return hit.List;
        string body;
        using (var req = Request(HttpMethod.Get, "/v1/voices", c))
        using (var r = await SendRawAsync(req))
        {
            body = await r.Content.ReadAsStringAsync();
            if (r.StatusCode == HttpStatusCode.Unauthorized && body.Contains("missing_permissions"))
            {
                // 新建的 Key 默认限制权限，没开「Voices（读取）」时读不了账号里的音色（合成不受影响）；不带 Key 能取到官方预置音色
                using var open = new HttpRequestMessage(HttpMethod.Get, Api + "/v1/voices");
                using var r2 = await SendAsync(open, CancellationToken.None);
                body = await r2.Content.ReadAsStringAsync();
            }
            else if (!r.IsSuccessStatusCode) throw Fail(r.StatusCode, body);
        }
        var list = JsonNode.Parse(body)!["voices"]?.AsArray().OfType<JsonObject>()
                       .Select(v => (Id: v.Str("voice_id"), Name: VoiceName(v)))
                       .Where(v => v.Id.Length > 0)
                       .Select(v => (v.Id, v.Name.Length > 0 ? v.Name : v.Id)).ToList() ?? [];
        _voices = (cacheKey, list);
        return list;
    }

    /// <summary>官方预置音色的名字带介绍（「Roger - Laid-Back, Casual, Resonant」），只留名字。</summary>
    internal static string VoiceName(JsonObject v)
    {
        var name = v.Str("name").Trim();
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        return v.Str("category") == "premade" && dash > 0 ? name[..dash] : name;
    }

    public override async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        CheckReady(c);
        var body = new JsonObject
        {
            ["text"] = text,
            ["model_id"] = S(c, "elevenlabs_model", VoicePresets.ElevenLabsModels[0].Id),
            ["voice_settings"] = new JsonObject { ["speed"] = Math.Clamp(c.Num("elevenlabs_speed"), 0.7, 1.2) },
        };
        var voice = Uri.EscapeDataString(S(c, "elevenlabs_voice", DefaultVoice));
        using var req = Request(HttpMethod.Post, $"/v1/text-to-speech/{voice}?output_format=mp3_44100_128", c);
        req.Content = Json(body);
        using var r = await SendAsync(req, token);
        return Decode(await r.Content.ReadAsByteArrayAsync(token));
    }
}
