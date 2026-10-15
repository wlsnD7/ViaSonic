using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ViaTTS.Audio;

namespace ViaTTS.Engines;

/// <summary>阿里云百炼：Qwen3-TTS 走 HTTP，返回音频地址再下载；CosyVoice 走 WebSocket，音频以二进制帧返回。</summary>
public sealed class Bailian(Func<JsonObject> cfg) : OnlineEngine(cfg)
{
    public override string Key => "bailian";
    public override string Label => T("阿里云百炼");

    public static VoiceItem[] Presets(string model) => model == "cosy" ? VoicePresets.Cosy : VoicePresets.Qwen;

    public override string CacheKey(JsonObject c) => $"bailian|{c["bailian_region"]}|{c["bailian_model"]}|{c["bailian_voice"]}|{c["bailian_speed"]}";

    private static string Host(JsonObject c) => S(c, "bailian_region") == "intl" ? "dashscope-intl.aliyuncs.com" : "dashscope.aliyuncs.com";

    public override Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        CheckReady(c);
        var model = S(c, "bailian_model", "qwen");
        var voice = S(c, "bailian_voice", Presets(model)[0].Id);
        return model == "cosy" ? CosyAsync(text, voice, c, token) : QwenAsync(text, voice, c, token);
    }

    private async Task<Clip> QwenAsync(string text, string voice, JsonObject c, CancellationToken token)
    {
        var body = new JsonObject
        {
            ["model"] = "qwen3-tts-flash",
            ["input"] = new JsonObject { ["text"] = text, ["voice"] = voice, ["language_type"] = "Auto" },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{Host(c)}/api/v1/services/aigc/multimodal-generation/generation")
        {
            Content = Json(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey(c));
        string url;
        using (var r = await SendAsync(req, token))
        {
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync(token))!;
            url = j["output"]?["audio"]?["url"]?.GetValue<string>() ?? "";
            if (url.Length == 0) throw new TtsException(T("{0} 没有返回音频", Label));
        }
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        using var audio = await SendAsync(get, token);
        return Decode(await audio.Content.ReadAsByteArrayAsync(token));
    }

    private async Task<Clip> CosyAsync(string text, string voice, JsonObject c, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "bearer " + ApiKey(c));
        ws.Options.CollectHttpResponseDetails = true;
        try
        {
            await ws.ConnectAsync(new Uri($"wss://{Host(c)}/api-ws/v1/inference"), ct);
        }
        catch (WebSocketException e)
        {
            if (ws.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new TtsException(T("API Key 无效或已过期"));
            throw new TtsException(T("{0} 请求失败：{1}", Label, e.Message));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TtsException(T("{0} 请求失败：{1}", Label, T("连接超时")));
        }

        var task = Guid.NewGuid().ToString("N");
        JsonObject Header(string action) => new() { ["action"] = action, ["task_id"] = task, ["streaming"] = "duplex" };
        async Task Send(JsonObject o) => await ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, ct);

        await Send(new JsonObject
        {
            ["header"] = Header("run-task"),
            ["payload"] = new JsonObject
            {
                ["task_group"] = "audio", ["task"] = "tts", ["function"] = "SpeechSynthesizer", ["model"] = "cosyvoice-v3-flash",
                ["parameters"] = new JsonObject
                {
                    ["text_type"] = "PlainText", ["voice"] = voice, ["format"] = "mp3", ["sample_rate"] = 24000,
                    ["rate"] = Math.Clamp(N(c, "bailian_speed"), 0.5, 2.0),
                },
                ["input"] = new JsonObject(),
            },
        });

        var audio = new MemoryStream();
        var buf = new byte[64 * 1024];
        var msg = new MemoryStream();
        try
        {
            while (true)
            {
                msg.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) throw new TtsException(T("{0} 没有返回音频", Label));
                    msg.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    msg.Position = 0;
                    msg.CopyTo(audio);
                    continue;
                }
                var head = JsonNode.Parse(msg.ToArray())?["header"];
                switch (head?["event"]?.GetValue<string>())
                {
                    case "task-started":
                        await Send(new JsonObject { ["header"] = Header("continue-task"), ["payload"] = new JsonObject { ["input"] = new JsonObject { ["text"] = text } } });
                        await Send(new JsonObject { ["header"] = Header("finish-task"), ["payload"] = new JsonObject { ["input"] = new JsonObject() } });
                        break;
                    case "task-failed":
                        throw new TtsException(T("{0} 请求失败：{1}", Label,
                            head["error_message"]?.GetValue<string>() ?? head["error_code"]?.GetValue<string>() ?? ""));
                    case "task-finished":
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                        return Decode(audio.ToArray());
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TtsException(T("{0} 请求失败：{1}", Label, T("连接超时")));
        }
        catch (WebSocketException e)
        {
            throw new TtsException(T("{0} 请求失败：{1}", Label, e.Message));
        }
    }
}
