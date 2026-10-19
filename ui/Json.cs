using System.Text.Json.Nodes;

namespace ViaSonic;

/// <summary>
/// 读 config.json 和其中条目（常用语、模型、音色……）的字段。缺字段、类型不对时返回默认值，不抛异常；
/// 对 null 调用也安全，所以可以直接写 p.Str("id")、cfg["overlay"].Bool("hide_after_send")。
/// </summary>
public static class Json
{
    public static string Str(this JsonNode? n, string key, string def = "") =>
        Field(n, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;

    public static bool Bool(this JsonNode? n, string key, bool def = false) =>
        Field(n, key) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

    public static double Num(this JsonNode? n, string key) => ToDouble(Field(n, key));

    /// <summary>整数、小数都按 double 读；不是数字时为 0。</summary>
    public static double ToDouble(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return 0;
    }

    private static JsonNode? Field(JsonNode? n, string key) => n is JsonObject o ? o[key] : null;
}
