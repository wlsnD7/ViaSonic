using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

/// <summary>MiniMax 语音：音色列表包括系统音色和自己复刻、生成的音色。返回十六进制编码的 MP3。</summary>
public sealed class MiniMax(Func<JsonObject> cfg) : OnlineEngine(cfg)
{
    private static (string Key, List<(string Id, string Name)> List)? _voices;

    public override string Key => "minimax";
    public override string Label => "MiniMax";

    public override string CacheKey(JsonObject c) => $"minimax|{c["minimax_region"]}|{c["minimax_model"]}|{c["minimax_voice"]}|{c["minimax_speed"]}";

    private static string Host(JsonObject c) => S(c, "minimax_region") == "intl" ? "https://api.minimax.io" : "https://api.minimax.cn";

    private HttpRequestMessage Post(string path, JsonObject c, JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Host(c) + path) { Content = Json(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(c));
        return req;
    }

    /// <summary>MiniMax 出错时 HTTP 仍是 200，错误在 base_resp 里。</summary>
    private JsonNode Check(string body)
    {
        var j = JsonNode.Parse(body)!;
        int code = j["base_resp"]?["status_code"]?.GetValue<int>() ?? 0;
        if (code == 0) return j;
        if (code is 1004 or 2049) throw new TtsException(T("API Key 无效或已过期"));
        throw new TtsException(T("{0} 请求失败：{1}", Label, j["base_resp"]?["status_msg"]?.GetValue<string>() ?? code.ToString()));
    }

    public async Task<List<(string Id, string Name)>> VoicesAsync(JsonObject c, bool refresh = false)
    {
        var cacheKey = $"{ApiKey(c)}|{S(c, "minimax_region")}";
        if (!refresh && _voices is { } hit && hit.Key == cacheKey) return hit.List;
        using var req = Post("/v1/get_voice", c, new JsonObject { ["voice_type"] = "all" });
        using var r = await SendAsync(req, CancellationToken.None);
        var j = Check(await r.Content.ReadAsStringAsync());
        var list = new List<(string Id, string Name)>();
        // 复刻和生成的音色没有名称，用 ID 显示，放在前面
        foreach (var k in new[] { "voice_cloning", "voice_generation", "system_voice" })
            foreach (var v in j[k]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var id = v.Str("voice_id");
                if (id.Length > 0) list.Add((id, v.Str("voice_name") is { Length: > 0 } n ? n : id));
            }
        _voices = (cacheKey, list);
        return list;
    }

    public override async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        CheckReady(c);
        var body = new JsonObject
        {
            ["model"] = S(c, "minimax_model", VoicePresets.MiniMaxModels[0]),
            ["text"] = text,
            ["stream"] = false,
            ["voice_setting"] = new JsonObject
            {
                ["voice_id"] = S(c, "minimax_voice", "male-qn-qingse"),
                ["speed"] = Math.Clamp(c.Num("minimax_speed"), 0.5, 2.0),
            },
            ["audio_setting"] = new JsonObject { ["format"] = "mp3", ["sample_rate"] = 32000, ["bitrate"] = 128000, ["channel"] = 1 },
            ["output_format"] = "hex",
        };
        using var req = Post("/v1/t2a_v2", c, body);
        using var r = await SendAsync(req, token);
        var j = Check(await r.Content.ReadAsStringAsync(token));
        var hex = j["data"].Str("audio");
        return Decode(Convert.FromHexString(hex));
    }
}
