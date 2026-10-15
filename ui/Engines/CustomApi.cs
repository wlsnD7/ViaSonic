using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using ViaTTS.Audio;

namespace ViaTTS.Engines;

/// <summary>
/// 自定义接口：任意直接返回音频的 HTTP 接口（本地的 GPT-SoVITS api_v2、IndexTTS、CosyVoice 等）。
/// 地址里的 {text} 做 URL 编码；请求体是 JSON 时按 JSON 字符串转义，否则按表单编码。请求头加密保存（可能带 Key）。
/// </summary>
public sealed class CustomApi(Func<JsonObject> cfg) : OnlineEngine(cfg)
{
    private static readonly HttpClient LongHttp = new() { Timeout = TimeSpan.FromSeconds(120) }; // 本地模型可能很慢

    public override string Key => "custom";
    public override string Label => T("自定义接口");
    public override bool Paid => false;
    protected override bool NeedsKey => false;

    public override string Missing(JsonObject c) => S(c, "custom_url").Trim().Length == 0 ? T("还没有填写地址") : "";

    public override string CacheKey(JsonObject c) =>
        $"custom|{c["custom_method"]}|{c["custom_url"]}|{c["custom_body"]}|{Secrets.Get(c, "custom_headers").GetHashCode()}";

    public override async Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token)
    {
        CheckReady(c);
        bool post = S(c, "custom_method", "GET") == "POST";
        var url = S(c, "custom_url").Trim().Replace("{text}", Uri.EscapeDataString(text));
        using var req = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, url);
        string? contentType = null;
        foreach (var line in Secrets.Get(c, "custom_headers").Split('\n'))
        {
            int i = line.IndexOf(':');
            if (i <= 0) continue;
            var (name, value) = (line[..i].Trim(), line[(i + 1)..].Trim());
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
            else req.Headers.TryAddWithoutValidation(name, value);
        }
        if (post)
        {
            var tpl = S(c, "custom_body");
            bool json = tpl.TrimStart().StartsWith('{') || tpl.TrimStart().StartsWith('[');
            var body = tpl.Replace("{text}", json ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text) : Uri.EscapeDataString(text));
            req.Content = new StringContent(body, Encoding.UTF8);
            req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                contentType ?? (json ? "application/json" : "application/x-www-form-urlencoded"));
        }

        HttpResponseMessage r;
        try
        {
            r = await LongHttp.SendAsync(req, token);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TtsException(T("{0} 请求失败：{1}", Label, T("连接超时")));
        }
        catch (HttpRequestException e)
        {
            throw new TtsException(T("{0} 请求失败：{1}", Label, e.Message));
        }
        using (r)
        {
            var data = await r.Content.ReadAsByteArrayAsync(token);
            if (!r.IsSuccessStatusCode) throw Fail(r.StatusCode, Encoding.UTF8.GetString(data));
            if (!IsAudio(data))
            {
                var s = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 400)).Trim();
                throw new TtsException(T("接口返回的不是音频：{0}", s.Length > 200 ? s[..200] : s));
            }
            return Decode(data);
        }
    }
}
