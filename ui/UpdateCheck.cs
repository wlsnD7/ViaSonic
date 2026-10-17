using System.Text.Json.Nodes;

namespace ViaSonic;

/// <summary>
/// 查 GitHub 和 Gitee 上的最新 Release（标签格式 v1.2.3），只提醒，不自动下载。
/// 国内访问 GitHub 不稳定，两边同时查，任何一边查到新版就提示；下载页用响应更快的那边（国内一般是 Gitee，国外一般是 GitHub）。
/// </summary>
public static class UpdateCheck
{
    private const string GitHubApi = "https://api.github.com/repos/wlsnD7/ViaSonic/releases/latest";
    private const string GiteeApi = "https://gitee.com/api/v5/repos/wlsnD7/ViaSonic/releases/latest";
    private const string GiteePage = "https://gitee.com/wlsnD7/ViaSonic/releases/tag/";

    public record Release(string Tag, string Url);

    /// <param name="Newer">比当前新的版本，没有时为 null。</param>
    /// <param name="Ok">至少有一边查询成功。</param>
    public record Result(Release? Newer, bool Ok);

    public static async Task<Result> CheckAsync(string current)
    {
        var github = Query(GitHubApi, r => r["html_url"]?.GetValue<string>() ?? "", current);
        var gitee = Query(GiteeApi, r => GiteePage + Uri.EscapeDataString(r["tag_name"]?.GetValue<string>() ?? ""), current);
        var found = (await Task.WhenAll(github, gitee)).ToList();
        var ok = found.Where(r => r != null).Select(r => r!).ToList();
        if (ok.Count == 0) return new Result(null, false);
        if (!Version.TryParse(current, out var mine)) return new Result(null, true);
        var newest = ok.Where(r => r.Version > mine && r.Url.Length > 0).OrderByDescending(r => r.Version).ToList();
        if (newest.Count == 0) return new Result(null, true);
        // 同一个最新版本两边都有时，用响应更快的那边
        var top = newest[0].Version;
        var pick = newest.Where(r => r.Version == top).OrderBy(r => r.Elapsed).First();
        return new Result(new Release(pick.Tag, pick.Url), true);
    }

    private sealed record Found(string Tag, Version Version, string Url, TimeSpan Elapsed);

    private static async Task<Found?> Query(string api, Func<JsonNode, string> url, string current)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ViaSonic/{current}");
            if (api == GitHubApi) http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var r = JsonNode.Parse(await http.GetStringAsync(api))!;
            var tag = r["tag_name"]?.GetValue<string>() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
            return new Found(tag, v, url(r), watch.Elapsed);
        }
        catch (Exception e)
        {
            Log.Info(T("检查更新失败：{0}", $"{new Uri(api).Host}: {e.Message}"));
            return null;
        }
    }
}
