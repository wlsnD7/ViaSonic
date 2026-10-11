using System.Text.Json.Nodes;

namespace ViaTTS;

/// <summary>启动时查一次 GitHub 上的最新 Release（标签格式 v1.2.3），只提醒，不自动下载。</summary>
public static class UpdateCheck
{
    private const string LatestUrl = "https://api.github.com/repos/wlsnD7/ViaTTS/releases/latest";

    public record Release(string Tag, string Url);

    /// <summary>有比当前更新的版本时返回它，否则（包括没联网、查询失败）返回 null。</summary>
    public static async Task<Release?> NewerAsync(string current)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ViaTTS/{current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var r = JsonNode.Parse(await http.GetStringAsync(LatestUrl))!;
            var tag = r["tag_name"]?.GetValue<string>() ?? "";
            var url = r["html_url"]?.GetValue<string>() ?? "";
            if (Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && Version.TryParse(current, out var mine)
                && latest > mine && url.Length > 0)
                return new Release(tag, url);
        }
        catch (Exception e)
        {
            Log.Info(T("检查更新失败：{0}", e.Message));
        }
        return null;
    }
}
