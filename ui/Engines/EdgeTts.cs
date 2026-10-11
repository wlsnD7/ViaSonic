using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using ViaTTS.Audio;

namespace ViaTTS.Engines;

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

/// <summary>微软 Edge 朗读接口（免费，需联网）。协议参照 rany2/edge-tts。</summary>
public sealed class EdgeTts : ITtsEngine
{
    public static readonly (string Id, string Name)[] Voices =
    [
        ("zh-CN-YunxiNeural", "云希 · 男"),
        ("zh-CN-YunjianNeural", "云健 · 男"),
        ("zh-CN-YunyangNeural", "云扬 · 男"),
        ("zh-CN-YunxiaNeural", "云夏 · 童声"),
        ("zh-CN-XiaoxiaoNeural", "晓晓 · 女"),
        ("zh-CN-XiaoyiNeural", "晓伊 · 女"),
        ("zh-CN-liaoning-XiaobeiNeural", "晓北 · 东北话"),
        ("zh-CN-shaanxi-XiaoniNeural", "晓妮 · 陕西话"),
        ("zh-TW-HsiaoChenNeural", "曉臻 · 台湾"),
        ("zh-HK-WanLungNeural", "雲龍 · 粤语"),
        ("en-US-GuyNeural", "Guy · 英语"),
        ("ja-JP-KeitaNeural", "圭太 · 日语"),
    ];

    private const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string ChromiumFull = "143.0.3650.75";
    private const string ChromiumMajor = "143";
    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private static double _clockSkew;

    public string Key => "edge";
    public string Label => "Edge TTS";
    public bool Ready => true;
    public bool Loading => false;
    public string Error => "";
    public string ErrorKind => "";

    public string CacheKey(JsonObject c) => $"edge|{c["edge_voice"]}|{c["edge_rate"]}";
    public void Load(JsonObject cfg) { }

    /// <summary>Sec-MS-GEC：Windows 文件时间向下取整到 5 分钟，拼上 token 后 SHA256。</summary>
    private static string SecMsGec()
    {
        double ticks = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + _clockSkew + 11644473600;
        ticks -= ticks % 300;
        ticks *= 1e7;
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"{ticks:F0}{TrustedClientToken}"));
        return Convert.ToHexString(hash);
    }

    private static string JsDate() =>
        DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
        + " GMT+0000 (Coordinated Universal Time)";

    private static string CleanText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(ch is (>= '\0' and <= '\x08') or (>= '\x0B' and <= '\x0C') or (>= '\x0E' and <= '\x1F') ? ' ' : ch);
        return SecurityElement.Escape(sb.ToString()) ?? "";
    }

    public async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        try
        {
            return await SynthOnceAsync(text, c, token);
        }
        catch (TtsException e) when (e.Kind == "skew")
        {
            return await SynthOnceAsync(text, c, token); // 系统时间不准时按服务器时间修正后重试一次
        }
    }

    private async Task<Clip> SynthOnceAsync(string text, JsonObject c, CancellationToken token)
    {
        var voice = c["edge_voice"]?.GetValue<string>() ?? "zh-CN-YunxiNeural";
        int rate = (int)App.ToDouble(c["edge_rate"]);
        string Id() => Guid.NewGuid().ToString("N");

        using var ws = new ClientWebSocket();
        var o = ws.Options;
        o.SetRequestHeader("Pragma", "no-cache");
        o.SetRequestHeader("Cache-Control", "no-cache");
        o.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        o.SetRequestHeader("User-Agent", $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumMajor}.0.0.0 Safari/537.36 Edg/{ChromiumMajor}.0.0.0");
        o.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        o.SetRequestHeader("Cookie", $"muid={Convert.ToHexString(RandomNumberGenerator.GetBytes(16))};");
        o.CollectHttpResponseDetails = true;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var url = $"{WssUrl}?TrustedClientToken={TrustedClientToken}&ConnectionId={Id()}&Sec-MS-GEC={SecMsGec()}&Sec-MS-GEC-Version=1-{ChromiumFull}";
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
        }
        catch (WebSocketException e)
        {
            if (ws.HttpStatusCode == System.Net.HttpStatusCode.Forbidden
                && ws.HttpResponseHeaders?.TryGetValue("Date", out var d) == true
                && DateTimeOffset.TryParse(d.FirstOrDefault(), out var server))
            {
                _clockSkew += (server - DateTimeOffset.UtcNow).TotalSeconds;
                throw new TtsException("时间偏差", "skew");
            }
            throw new TtsException($"Edge TTS 连接失败：{e.Message}");
        }

        async Task Send(string s) => await ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, ct);
        await Send($"X-Timestamp:{JsDate()}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n"
                   + "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\"},"
                   + "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n");
        var ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
                   + $"<voice name='{voice}'><prosody pitch='+0Hz' rate='{rate:+0;-0;+0}%' volume='+0%'>{CleanText(text)}</prosody></voice></speak>";
        await Send($"X-RequestId:{Id()}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{JsDate()}Z\r\nPath:ssml\r\n\r\n{ssml}");

        var audio = new MemoryStream();
        var buf = new byte[64 * 1024];
        var msg = new MemoryStream();
        while (true)
        {
            msg.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) goto done;
                msg.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            var data = msg.ToArray();
            if (r.MessageType == WebSocketMessageType.Text)
            {
                var s = Encoding.UTF8.GetString(data);
                int end = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if ((end < 0 ? s : s[..end]).Contains("Path:turn.end")) break;
            }
            else if (data.Length >= 2)
            {
                // 二进制帧：前 2 字节是头部长度（大端），头部之后是 MP3 数据
                int hl = data[0] << 8 | data[1];
                if (hl + 2 > data.Length) continue;
                var head = Encoding.UTF8.GetString(data, 2, hl);
                if (head.Contains("Path:audio") && head.Contains("Content-Type:audio/mpeg"))
                    audio.Write(data, hl + 2, data.Length - hl - 2);
            }
        }
        done:
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        if (audio.Length == 0) throw new TtsException("Edge TTS 没有生成声音，所选音色可能读不了这种语言");
        return AudioDecode.FromCompressed(audio.ToArray());
    }
}
