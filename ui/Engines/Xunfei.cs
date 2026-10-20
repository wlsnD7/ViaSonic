using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

/// <summary>讯飞开放平台：在线语音合成（流式版，WebSocket）。音色要先在讯飞控制台添加；APPID、API Key、API Secret 和语音输入共用。</summary>
public sealed class Xunfei(Func<JsonObject> cfg) : OnlineEngine(cfg)
{
    public const string TtsHost = "tts-api.xfyun.cn";

    public override string Key => "xunfei";
    public override string Label => T("讯飞语音");

    public override string Missing(JsonObject c) => XunfeiWs.Missing(c) is { Length: > 0 } m ? m : base.Missing(c);

    public override string CacheKey(JsonObject c) => $"xunfei|{c["xunfei_voice"]}|{c["xunfei_rate"]}";

    public override async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        CheckReady(c);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var ws = await XunfeiWs.ConnectAsync(TtsHost, "/v2/tts", c, Label, timeout.Token);
        var req = new JsonObject
        {
            ["common"] = new JsonObject { ["app_id"] = S(c, "xunfei_appid").Trim() },
            ["business"] = new JsonObject
            {
                ["aue"] = "raw", // 16kHz 16 位 PCM，不用解码
                ["auf"] = "audio/L16;rate=16000",
                ["vcn"] = S(c, "xunfei_voice", VoicePresets.Xunfei[0].Id),
                ["speed"] = Speed(c),
                ["tte"] = "UTF8",
            },
            ["data"] = new JsonObject { ["status"] = 2, ["text"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) },
        };
        await XunfeiWs.SendAsync(ws, req, timeout.Token);
        var pcm = new MemoryStream();
        while (true)
        {
            var j = await XunfeiWs.ReceiveAsync(ws, Label, timeout.Token);
            if (j["data"]?["audio"] is JsonValue a && a.TryGetValue<string>(out var b64) && b64.Length > 0)
                pcm.Write(Convert.FromBase64String(b64));
            if (j["data"]?["status"]?.GetValue<int>() == 2) break;
        }
        var bytes = pcm.ToArray();
        if (bytes.Length < 2) throw new TtsException(T("{0} 没有生成声音，所选音色可能读不了这种语言", Label));
        var samples = new float[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return new Clip(samples, 16000);
    }

    /// <summary>界面上的语速是 -50%~+50%，讯飞是 0~100（50 是正常）。</summary>
    internal static int Speed(JsonObject c) => Math.Clamp(50 + (int)c.Num("xunfei_rate"), 0, 100);
}

/// <summary>讯飞 WebSocket 接口的共同部分：HMAC-SHA256 签名的地址、收发 JSON、错误码。</summary>
public static class XunfeiWs
{
    /// <summary>APPID、API Key、API Secret 缺哪个。</summary>
    public static string Missing(JsonObject c) =>
        c.Str("xunfei_appid").Trim().Length == 0 ? T("请先填写 APPID")
        : Secrets.Get(c, "xunfei_key").Length == 0 ? T("未填写 API Key")
        : Secrets.Get(c, "xunfei_secret").Length == 0 ? T("请先填写 API Secret")
        : "";

    /// <summary>签名后的 wss 地址：用 API Secret 对 host、date、请求行做 HMAC-SHA256，和 API Key 一起放进 authorization 参数。</summary>
    internal static string SignedUrl(string host, string path, string apiKey, string apiSecret, DateTime utcNow)
    {
        var date = utcNow.ToString("r");
        var origin = $"host: {host}\ndate: {date}\nGET {path} HTTP/1.1";
        var sign = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiSecret), Encoding.UTF8.GetBytes(origin)));
        var auth = $"api_key=\"{apiKey}\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", signature=\"{sign}\"";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(auth));
        return $"wss://{host}{path}?authorization={Uri.EscapeDataString(b64)}&date={Uri.EscapeDataString(date)}&host={Uri.EscapeDataString(host)}";
    }

    public static async Task<ClientWebSocket> ConnectAsync(string host, string path, JsonObject c, string label, CancellationToken token)
    {
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        var url = SignedUrl(host, path, Secrets.Get(c, "xunfei_key"), Secrets.Get(c, "xunfei_secret"), DateTime.UtcNow);
        try
        {
            await ws.ConnectAsync(new Uri(url), token);
            return ws;
        }
        catch (WebSocketException e)
        {
            var status = ws.HttpStatusCode;
            ws.Dispose();
            // 401：签名不对（Key / Secret 填错）；403：时间偏差太大或没开通
            if (status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new TtsException(T("API Key 无效或已过期"));
            throw new TtsException(T("{0} 请求失败：{1}", label, e.Message));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TtsException(T("{0} 请求失败：{1}", label, T("连接超时")));
        }
    }

    public static Task SendAsync(ClientWebSocket ws, JsonNode o, CancellationToken token) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, token);

    /// <summary>收一条 JSON；code 不是 0 时转成提示。</summary>
    public static async Task<JsonNode> ReceiveAsync(ClientWebSocket ws, string label, CancellationToken token)
    {
        var buf = new byte[1 << 16];
        using var msg = new MemoryStream();
        WebSocketReceiveResult r;
        try
        {
            do
            {
                r = await ws.ReceiveAsync(buf, token);
                if (r.MessageType == WebSocketMessageType.Close) throw new TtsException(T("{0} 请求失败：{1}", label, ws.CloseStatusDescription ?? ""));
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TtsException(T("{0} 请求失败：{1}", label, T("连接超时")));
        }
        catch (WebSocketException e)
        {
            throw new TtsException(T("{0} 请求失败：{1}", label, e.Message));
        }
        var j = JsonNode.Parse(msg.ToArray()) ?? new JsonObject();
        int code = j["code"]?.GetValue<int>() ?? 0;
        if (code != 0) throw Error(code, j.Str("message"), label);
        return j;
    }

    internal static TtsException Error(int code, string message, string label) => code switch
    {
        11200 => new TtsException(T("这个音色还没有开通，请先在讯飞控制台添加")),
        _ => new TtsException(T("{0} 请求失败：{1}", label, message.Length > 0 ? $"{message} ({code})" : code.ToString())),
    };
}
