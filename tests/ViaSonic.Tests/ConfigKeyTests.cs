using System.Text.RegularExpressions;

namespace ViaSonic.Tests;

/// <summary>
/// 配置是按名字读写的 JSON，键名拼错不会报错，只会悄悄读到默认值。这里扫一遍源码，
/// 代码和 XAML 里用到的配置键都必须在 ConfigStore 的默认值里。
/// </summary>
public class ConfigKeyTests : IDisposable
{
    public void Dispose() => Lang.Init("zh");

    // A.Str("x") / C.Bool("x") / cfg.Num("x") / A.Set("x", …) / S(c, "x") / Secrets.Get(c, "x") / Config["x"]
    private static readonly Regex[] CodePatterns =
    [
        new(@"\b(?:A|C|c|cfg|Config|_app)\.(?:Str|Bool|Num|Set|Sub|Commit)\(""(?<k>[a-z0-9_]+)"""),
        new(@"\b(?:A\.|C|c|cfg)?Config\[""(?<k>[a-z0-9_]+)""\]"),
        new(@"\bS\(c, ""(?<k>[a-z0-9_]+)"""),
        new(@"Secrets\.Get\([\w.]+, ""(?<k>[a-z0-9_]+)""\)"),
    ];

    // App 自己的方法不带前缀：Str("x")、Set("x", …)
    private static readonly Regex AppPattern = new(@"(?<![\w.])(?:Str|Bool|Num|Set|Sub|Commit)\(""(?<k>[a-z0-9_]+)""");

    // 通用的设置控件用 Tag 写配置键（HomePage 的 Choice_Changed、Rate_Changed 等）
    private static readonly Regex TagPattern = new(@"Tag=""(?<k>[a-z]+_[a-z0-9_]+)""");

    private static IEnumerable<(string File, string Key)> UsedKeys()
    {
        var ui = Path.Combine(Signal.RepoRoot(), "ui");
        foreach (var f in Directory.EnumerateFiles(ui, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(ui, f);
            if (rel.StartsWith("bin") || rel.StartsWith("obj")) continue;
            var text = File.ReadAllText(f);
            IEnumerable<Regex> patterns = f.EndsWith(".cs")
                ? Path.GetFileName(f).StartsWith("App.") ? [.. CodePatterns, AppPattern] : CodePatterns
                : f.EndsWith(".xaml") ? [TagPattern] : [];
            foreach (var p in patterns)
                foreach (Match m in p.Matches(text))
                    yield return (rel, m.Groups["k"].Value);
        }
    }

    [Fact]
    public void EveryUsedKeyHasDefault()
    {
        var defaults = ConfigStore.Normalize(null, "zh");
        var known = defaults.Select(kv => kv.Key)
            // XAML 的 Tag 也用来指子对象和条目的字段：输入框设置、GPT-SoVITS 模型
            .Concat(defaults["overlay"]!.AsObject().Select(kv => kv.Key))
            .Concat(ConfigStore.NewModel().Select(kv => kv.Key))
            // Commit 的参数是「改了什么」：常用语的音量、循环改了，不用像 phrases 那样重新注册快捷键
            .Append("phrase_props")
            .ToHashSet();
        var used = UsedKeys().ToList();
        Assert.True(used.Count > 100, $"只找到 {used.Count} 处，扫描的写法可能变了");
        var unknown = used.Where(u => !known.Contains(u.Key)).Select(u => $"{u.File}: {u.Key}").Distinct().ToList();
        Assert.True(unknown.Count == 0, "没有默认值的配置键：\n" + string.Join("\n", unknown));
    }
}
