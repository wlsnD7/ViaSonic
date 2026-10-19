using System.Text.Json.Nodes;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

/// <summary>火山引擎豆包语音：V3 HTTP 单向流式接口，新版控制台的 API Key 鉴权。1.0 和 2.0 的音色要用不同的资源 ID。</summary>
public sealed class Doubao(Func<JsonObject> cfg) : OnlineEngine(cfg)
{
    private const string Url = "https://openspeech.bytedance.com/api/v3/tts/unidirectional";

    public override string Key => "doubao";
    public override string Label => T("豆包语音");

    /// <summary>预置音色；声音复刻 2.0（icl2）没有预置，只有自己添加的 S_ 开头的复刻音色。</summary>
    public static VoiceItem[] Presets(string model) => model switch
    {
        "1.0" => VoicePresets.Doubao1,
        "icl2" => [],
        _ => VoicePresets.Doubao2,
    };

    public override string Missing(JsonObject c) =>
        base.Missing(c) is { Length: > 0 } m ? m
        : S(c, "doubao_model", "2.0") == "icl2" && S(c, "doubao_voice").Length == 0 ? T("还没有音色，点「音色」右边的 + 添加") : "";

    public override string CacheKey(JsonObject c) => $"doubao|{c["doubao_model"]}|{c["doubao_voice"]}|{c["doubao_rate"]}";

    public override async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        CheckReady(c);
        var model = S(c, "doubao_model", "2.0");
        var body = new JsonObject
        {
            ["user"] = new JsonObject { ["uid"] = "viasonic" },
            ["req_params"] = new JsonObject
            {
                ["text"] = text,
                ["speaker"] = S(c, "doubao_voice", Presets(model).FirstOrDefault()?.Id ?? ""),
                ["audio_params"] = new JsonObject
                {
                    ["format"] = "mp3",
                    ["sample_rate"] = 24000,
                    ["speech_rate"] = Math.Clamp((int)c.Num("doubao_rate"), -50, 100),
                },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = Json(body) };
        req.Headers.Add("X-Api-Key", ApiKey(c));
        req.Headers.Add("X-Api-Resource-Id", model switch { "1.0" => "seed-tts-1.0", "icl2" => "seed-icl-2.0", _ => "seed-tts-2.0" });
        req.Headers.Add("X-Api-Request-Id", Guid.NewGuid().ToString());
        using var r = await SendAsync(req, token);

        // 返回的是一串连在一起的 JSON 对象：code 0 带 base64 音频，20000000 表示结束，其他是错误
        var all = await r.Content.ReadAsStringAsync(token);
        var audio = new MemoryStream();
        foreach (var part in SplitObjects(all))
        {
            var j = JsonNode.Parse(part)!;
            int code = j["code"]?.GetValue<int>() ?? 0;
            if (code == 0)
            {
                if (j["data"] is JsonValue d && d.TryGetValue<string>(out var b64) && b64.Length > 0)
                    audio.Write(Convert.FromBase64String(b64));
            }
            else if (code != 20000000)
            {
                throw new TtsException(T("{0} 请求失败：{1}", Label, j["message"]?.GetValue<string>() ?? code.ToString()));
            }
        }
        return Decode(audio.ToArray());
    }

    /// <summary>把首尾相接的多个 JSON 对象拆开（字符串里的花括号不算）。</summary>
    private static IEnumerable<string> SplitObjects(string s)
    {
        int depth = 0, start = -1;
        bool str = false, esc = false;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (str)
            {
                if (esc) esc = false;
                else if (ch == '\\') esc = true;
                else if (ch == '"') str = false;
                continue;
            }
            if (ch == '"') str = true;
            else if (ch == '{')
            {
                if (depth++ == 0) start = i;
            }
            else if (ch == '}' && depth > 0 && --depth == 0)
            {
                yield return s[start..(i + 1)];
            }
        }
    }
}
