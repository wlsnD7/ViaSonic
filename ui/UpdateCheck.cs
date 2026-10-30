using System.Text.Json.Nodes;

namespace ViaSonic;

/// <summary>查 GitHub 上的最新 Release（标签格式 v1.2.3），只提醒，不自动下载。</summary>
public static class UpdateCheck
{
    private const string GitHubApi = "https://api.github.com/repos/wlsnD7/ViaSonic/releases/latest";

    public record Release(string Tag, string Url);

    /// <param name="Newer">比当前新的版本，没有时为 null。</param>
    /// <param name="Ok">查询成功。</param>
    public record Result(Release? Newer, bool Ok);

    public static async Task<Result> CheckAsync(string current)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ViaSonic/{current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var r = JsonNode.Parse(await http.GetStringAsync(GitHubApi))!;
            var tag = r.Str("tag_name");
            var url = r.Str("html_url");
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return new Result(null, false);
            if (!Version.TryParse(current, out var mine) || latest <= mine || url.Length == 0) return new Result(null, true);
            return new Result(new Release(tag, url), true);
        }
        catch (Exception e)
        {
            Log.Info(T("检查更新失败：{0}", $"{new Uri(GitHubApi).Host}: {e.Message}"));
            return new Result(null, false);
        }
    }
}
