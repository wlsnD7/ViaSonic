using System.Globalization;

namespace ViaSonic.Engines;

/// <summary>Edge 音色按语言分组：主页先选语言，再选这种语言的音色。数据在 EdgeVoices.Data.cs。</summary>
public static partial class EdgeVoices
{
    public sealed record Voice(string Id, string Lang, string Name);

    private const string Multi = "Multilingual";

    /// <summary>全部音色 ID。</summary>
    public static IEnumerable<string> Ids => Chinese.Select(v => v.Id).Concat(Others.Select(v => $"{v.Locale}-{v.Name}Neural"));

    /// <summary>音色属于哪种语言：粤语（zh-HK）单独一种，其他取 locale 的第一段。</summary>
    public static string LangOf(string id)
    {
        if (id.StartsWith("zh-HK", StringComparison.Ordinal)) return "yue";
        int i = id.IndexOf('-');
        return i > 0 ? id[..i] : id;
    }

    /// <summary>语言下拉框：中文、粤语、英语、日语、韩语在前，其余按名称排。</summary>
    public static List<(string Id, string Name)> Langs()
    {
        var head = LangNames.Take(5).Select(l => (l.Id, l.Name));
        var rest = LangNames.Skip(5).Select(l => (l.Id, l.Name)).OrderBy(l => l.Name, NameOrder());
        return [.. head, .. rest];
    }

    /// <summary>一种语言的全部音色（界面语言的名称）。</summary>
    public static List<(string Id, string Name)> Voices(string lang)
    {
        if (lang is "zh" or "yue") return Chinese.Where(v => LangOf(v.Id) == lang).Select(v => (v.Id, v.Name)).ToList();
        var rows = Others.Where(v => LangOf(v.Locale) == lang).ToList();
        return rows.Select(v => ($"{v.Locale}-{v.Name}Neural",
            Label(v.Name, rows.Count(o => o.Name == v.Name) > 1 ? RegionNames.FirstOrDefault(r => r.Id == v.Locale.Split('-')[1])?.Name : null))).ToList();
    }

    /// <summary>只显示名字，多语种音色标出来；同一种语言里重名时才在后面标区别，如「Siqiniq（音节文字）」。</summary>
    internal static string Label(string name, string? diff)
    {
        bool multi = name.EndsWith(Multi, StringComparison.Ordinal);
        if (multi) name = name[..^Multi.Length];
        bool en = Lang.Current == "en";
        var label = multi ? name + (en ? " Multilingual" : " 多语种") : name; // 音色名
        return diff == null ? label : en ? $"{label} ({diff})" : $"{label}（{diff}）";
    }

    /// <summary>中文界面按拼音排，英文界面按字母排。</summary>
    private static StringComparer NameOrder()
    {
        try
        {
            return StringComparer.Create(CultureInfo.GetCultureInfo(Lang.Current == "en" ? "en-US" : "zh-CN"), false);
        }
        catch (CultureNotFoundException)
        {
            return StringComparer.Ordinal;
        }
    }
}
