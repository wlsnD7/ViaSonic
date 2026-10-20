using System.Text;
using System.Text.Json;

namespace ViaSonic.Phone;

/// <summary>手机页面（phone.html，嵌在程序里）：按当前界面语言填入文字和令牌。</summary>
internal static class PhonePage
{
    private static readonly Lazy<string> Template = new(() =>
    {
        using var s = typeof(PhonePage).Assembly.GetManifestResourceStream("phone.html")!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    });

    private static readonly Lazy<byte[]?> IconBytes = new(() =>
    {
        try { return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "ViaSonic.png")); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    });

    public static byte[]? Icon => IconBytes.Value;

    /// <summary>token 为 null：链接里的令牌不对，页面只显示「链接已失效」。</summary>
    public static string Render(string? token)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) // 默认会转义 < > &，可以直接放进 <script>
        {
            w.WriteStartObject();
            if (token != null) w.WriteString("k", token);
            else w.WriteNull("k");
            w.WriteStartObject("t");
            w.WriteString("placeholder", T("输入文字"));
            w.WriteString("send", T("发送"));
            w.WriteString("stop", T("停止"));
            w.WriteString("repeat", T("重复"));
            w.WriteString("phrases", T("常用语"));
            w.WriteString("empty", T("还没有常用语"));
            w.WriteString("offline", T("无法连接到电脑，请检查 ViaSonic 手机输入是否开启。"));
            w.WriteString("expired", T("链接已失效，请重新扫码。"));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        var manifest = token != null ? $"<link rel=\"manifest\" href=\"manifest.webmanifest?k={token}\">" : "";
        return Template.Value
            .Replace("__LANG__", Lang.Current == "en" ? "en" : "zh-CN")
            .Replace("__MANIFEST__", manifest)
            .Replace("__DATA__", Encoding.UTF8.GetString(ms.ToArray()));
    }

    /// <summary>「添加到主屏幕」用：打开时带上令牌。</summary>
    public static byte[] Manifest(string token)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("name", "ViaSonic");
            w.WriteString("short_name", "ViaSonic");
            w.WriteString("start_url", "/?k=" + token);
            w.WriteString("display", "standalone");
            w.WriteString("background_color", "#0E1630");
            w.WriteString("theme_color", "#0E1630");
            w.WriteStartArray("icons");
            w.WriteStartObject();
            w.WriteString("src", "icon.png");
            w.WriteString("sizes", "256x256");
            w.WriteString("type", "image/png");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
