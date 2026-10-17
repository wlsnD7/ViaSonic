using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ViaSonic.Audio;

namespace ViaSonic.Engines;

/// <summary>联网服务的共同部分：API Key、HTTP 请求、错误提示。没填 Key 时不算就绪。</summary>
public abstract class OnlineEngine(Func<JsonObject> cfg) : ITtsEngine
{
    // 区域等填错时域名可能解析到不响应的地址，连接 10 秒没连上就放弃
    protected static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public abstract string Key { get; }
    public abstract string Label { get; }
    /// <summary>按量收费：不预生成常用语，第一次用到时再合成。</summary>
    public virtual bool Paid => true;
    public bool Ready => Missing(cfg()).Length == 0;
    public bool Loading => false;
    public string Error => MissingError(cfg());
    public string ErrorKind => "";
    protected virtual bool NeedsKey => true;

    private bool KeyMissing(JsonObject c) => NeedsKey && ApiKey(c).Length == 0;

    /// <summary>还缺什么设置，主页状态栏显示；空＝可以合成。</summary>
    public virtual string Missing(JsonObject c) => KeyMissing(c) ? T("未填写 API Key") : "";

    /// <summary>朗读时的提示。</summary>
    private string MissingError(JsonObject c) => KeyMissing(c) ? T("请先填写 {0} 的 API Key", Label) : Missing(c);

    /// <summary>设置不全时不发请求。</summary>
    protected void CheckReady(JsonObject c)
    {
        if (MissingError(c) is { Length: > 0 } m) throw new TtsException(m);
    }

    public abstract string CacheKey(JsonObject cfg);
    public void Load(JsonObject cfg) { }
    public abstract Task<Clip> SynthAsync(string text, JsonObject cfg, CancellationToken token);

    protected string ApiKey(JsonObject c) => Secrets.Get(c, Key + "_key");

    protected static string S(JsonObject c, string key, string def = "")
    {
        var v = c[key]?.GetValue<string>() ?? "";
        return v.Length > 0 ? v : def;
    }

    protected static double N(JsonObject c, string key) => App.ToDouble(c[key]);

    protected static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>发请求，失败时转成界面上的提示。</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken token)
    {
        HttpResponseMessage r;
        try
        {
            r = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TtsException(T("{0} 请求失败：{1}", Label, T("连接超时")));
        }
        catch (HttpRequestException e)
        {
            throw new TtsException(T("{0} 请求失败：{1}", Label, e.Message));
        }
        if (r.IsSuccessStatusCode) return r;
        var body = "";
        try { body = await r.Content.ReadAsStringAsync(token); } catch { }
        r.Dispose();
        throw Fail(r.StatusCode, body);
    }

    protected TtsException Fail(HttpStatusCode status, string body)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new TtsException(T("API Key 无效或已过期"));
        return new TtsException(T("{0} 请求失败：{1}", Label, ErrorText(body) is { Length: > 0 } m ? m : $"HTTP {(int)status}"));
    }

    /// <summary>从各家的错误 JSON 里找出说明文字。</summary>
    protected static string ErrorText(string body)
    {
        try
        {
            var j = JsonNode.Parse(body);
            foreach (var path in new[] { "message", "error_message", "status_msg", "error/message", "base_resp/status_msg", "detail" })
            {
                JsonNode? n = j;
                foreach (var p in path.Split('/')) n = n is JsonObject o ? o[p] : null;
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
            }
        }
        catch { }
        body = body.Trim();
        return body.Length > 200 ? body[..200] : body;
    }

    protected Clip Decode(byte[] data)
    {
        if (data.Length == 0) throw new TtsException(T("{0} 没有生成声音，所选音色可能读不了这种语言", Label));
        return IsWave(data) ? AudioDecode.FromWave(data) : AudioDecode.FromCompressed(data);
    }

    protected static bool IsWave(byte[] d) => d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F';

    /// <summary>看文件头是不是音频：wav / mp3 / ogg / flac。</summary>
    protected static bool IsAudio(byte[] d) =>
        d.Length > 4 && (IsWave(d)
                         || (d[0] == 'I' && d[1] == 'D' && d[2] == '3')
                         || (d[0] == 0xFF && (d[1] & 0xE0) == 0xE0)
                         || (d[0] == 'O' && d[1] == 'g' && d[2] == 'g' && d[3] == 'S')
                         || (d[0] == 'f' && d[1] == 'L' && d[2] == 'a' && d[3] == 'C'));
}

/// <summary>预置音色：中文界面显示中文名，英文界面显示英文名。</summary>
public sealed record VoiceItem(string Id, string Zh, string En)
{
    public string Name => Lang.Current == "en" ? En : Zh;
}
