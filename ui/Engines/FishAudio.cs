using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ViaTTS.Audio;

namespace ViaTTS.Engines;

/// <summary>Fish Audio：音色是社区或自己创建的模型，用模型 ID 指定。</summary>
public sealed class FishAudio(Func<JsonObject> cfg) : OnlineEngine(cfg)
{
    private const string Api = "https://api.fish.audio";

    public override string Key => "fish";
    public override string Label => "Fish Audio";

    public override string CacheKey(JsonObject c) => $"fish|{c["fish_voice"]}|{c["fish_speed"]}";

    private HttpRequestMessage Request(HttpMethod method, string path, JsonObject c)
    {
        var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(c));
        return req;
    }

    /// <summary>自己创建的音色（id, 名称）。</summary>
    public async Task<List<(string Id, string Name)>> MyVoicesAsync(JsonObject c)
    {
        using var req = Request(HttpMethod.Get, "/model?self=true&page_size=100&sort_by=created_at", c);
        using var r = await SendAsync(req, CancellationToken.None);
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        return j["items"]?.AsArray().OfType<JsonObject>()
                   .Select(m => (m["_id"]?.GetValue<string>() ?? "", m["title"]?.GetValue<string>() ?? ""))
                   .Where(m => m.Item1.Length > 0).ToList() ?? [];
    }

    /// <summary>按 ID 查音色名称，查不到返回 null。</summary>
    public async Task<string?> VoiceNameAsync(JsonObject c, string id)
    {
        try
        {
            using var req = Request(HttpMethod.Get, "/model/" + Uri.EscapeDataString(id), c);
            using var r = await SendAsync(req, CancellationToken.None);
            return JsonNode.Parse(await r.Content.ReadAsStringAsync())?["title"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public override async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        CheckReady(c);
        var body = new JsonObject
        {
            ["text"] = text,
            ["format"] = "mp3",
            ["prosody"] = new JsonObject { ["speed"] = Math.Clamp(N(c, "fish_speed"), 0.5, 2.0) },
        };
        var voice = S(c, "fish_voice");
        if (voice.Length > 0) body["reference_id"] = voice;
        using var req = Request(HttpMethod.Post, "/v1/tts", c);
        req.Content = Json(body);
        using var r = await SendAsync(req, token);
        return Decode(await r.Content.ReadAsByteArrayAsync(token));
    }
}
