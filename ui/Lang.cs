using System.Globalization;

namespace ViaTTS;

/// <summary>界面语言：以中文原文作键，英文查 Lang.En.cs 里的对照表，查不到就显示中文。</summary>
public static partial class Lang
{
    /// <summary>"zh" 或 "en"。配置读出来之前先按系统语言。</summary>
    public static string Current { get; private set; } = SystemDefault();

    public static event Action? Changed;

    public static string SystemDefault() =>
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";

    public static string T(string zh) => Current == "en" && En.TryGetValue(zh, out var en) ? en : zh;

    public static string T(string zh, params object?[] args) => string.Format(T(zh), args);

    /// <summary>启动时从配置设置，不触发 Changed。</summary>
    public static void Init(string lang) => Current = lang == "en" ? "en" : "zh";

    public static void Set(string lang)
    {
        lang = lang == "en" ? "en" : "zh";
        if (lang == Current) return;
        Current = lang;
        Changed?.Invoke();
    }
}
