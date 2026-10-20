using System.Text.Json.Nodes;
using ViaSonic.Engines;

namespace ViaSonic;

/// <summary>
/// 随机音色：每次朗读先随机选一个可用的引擎，再在这个引擎里随机选一个读得了这段文字的音色。
/// 联网服务没填 Key、GPT-SoVITS 没加载的跳过；GPT-SoVITS 只用当前加载的模型，自定义接口只有一个音色。
/// </summary>
public static class RandomVoice
{
    private static readonly Random Rng = new();
    private static string _last = "";
    private static (DateTime At, List<string> Ids)? _fish; // Fish Audio 自己的音色，每次都查太慢，缓存 10 分钟

    /// <summary>返回选中的引擎和改好音色的配置副本；一个都选不出来时返回 null（用当前引擎）。</summary>
    public static async Task<(ITtsEngine Engine, JsonObject Cfg)?> PickAsync(JsonObject cfg, string text, IReadOnlyDictionary<string, ITtsEngine> engines)
    {
        var lang = TextLang(text);
        var pool = new List<(string Engine, List<string> Voices)>();
        foreach (var (key, eng) in engines)
        {
            if (!eng.Ready) continue;
            List<string> voices;
            try { voices = await VoicesAsync(key, eng, cfg, lang); }
            catch (Exception e) { Log.Warn(e.Message); continue; }
            if (voices.Count > 0) pool.Add((key, voices));
        }
        if (pool.Count == 0) return null;

        // 尽量不和上一句重复
        for (int i = 0; i < 4; i++)
        {
            var (key, voices) = pool[Rng.Next(pool.Count)];
            var voice = voices[Rng.Next(voices.Count)];
            if ($"{key}|{voice}" == _last && i < 3 && pool.Sum(p => p.Voices.Count) > 1) continue;
            _last = $"{key}|{voice}";
            var c = cfg.DeepClone().AsObject();
            c["engine"] = key;
            if (VoiceKey(key) is { } vk) c[vk] = voice;
            if (key == "azure") c["azure_style"] = ""; // 风格只对原来的音色有效
            return (engines[key], c);
        }
        return null;
    }

    private static string? VoiceKey(string engine) => engine switch
    {
        "gsv" or "custom" => null,
        _ => engine + "_voice",
    };

    /// <summary>文字的主要语言：有汉字算中文，有假名算日语，有谚文算韩语，其他算英语。</summary>
    internal static string TextLang(string text)
    {
        bool han = false;
        foreach (var ch in text)
        {
            if (ch is >= '぀' and <= 'ヿ') return "ja";
            if (ch is >= '가' and <= '힯') return "ko";
            if (ch is >= '一' and <= '鿿') han = true;
        }
        return han ? "zh" : "en";
    }

    /// <summary>locale 为空＝不知道语言，当作都能读。</summary>
    private static bool Fits(string locale, string lang) =>
        locale.Length == 0 || locale.StartsWith(lang, StringComparison.OrdinalIgnoreCase);

    private static async Task<List<string>> VoicesAsync(string key, ITtsEngine eng, JsonObject c, string lang)
    {
        List<string> Saved(string engine, string model = "") =>
            c[engine + "_voices"]?.AsArray().OfType<JsonObject>()
                .Where(v => engine == "fish" || v.Str("model") == model)
                .Select(v => v.Str("id")).Where(id => id.Length > 0).ToList() ?? [];
        // 豆包的音色 ID 以语言开头（zh_、en_ …），MiniMax 的英文音色以 English_ 开头
        static string IdLocale(string id)
        {
            var m = System.Text.RegularExpressions.Regex.Match(id, "^([a-z]{2})_");
            if (m.Success) return m.Groups[1].Value;
            foreach (var (prefix, l) in new[] { ("English_", "en"), ("Japanese_", "ja"), ("Korean_", "ko"), ("Chinese", "zh") })
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return l;
            return "";
        }

        switch (key)
        {
            case "edge":
                return EdgeTts.Voices.Select(v => v.Id).Where(id => Fits(id[..2], lang)).ToList();
            case "sapi":
                return Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                    .Where(v => Fits(v.Language, lang)).Select(v => v.Id).ToList();
            case "gsv":
            case "custom":
                return [""]; // 只有当前这一个
            case "azure":
                var az = await ((AzureTts)eng).VoicesAsync(c);
                return az.Where(v => v.Id.Contains("Multilingual") || Fits(v.Locale, lang)).Select(v => v.Id).ToList();
            case "fish":
                var fish = Saved("fish");
                if (_fish is not { } hit || DateTime.UtcNow - hit.At > TimeSpan.FromMinutes(10))
                {
                    try { _fish = (DateTime.UtcNow, (await ((FishAudio)eng).MyVoicesAsync(c)).Select(v => v.Id).ToList()); }
                    catch (Exception e) { Log.Warn(e.Message); _fish = (DateTime.UtcNow, []); }
                }
                fish.AddRange(_fish!.Value.Ids);
                if (c.Str("fish_voice").Length > 0) fish.Add(c.Str("fish_voice"));
                return fish.Distinct().ToList();
            case "doubao":
            case "bailian":
                var model = c.Str(key + "_model");
                var presets = key == "doubao" ? Doubao.Presets(model) : Bailian.Presets(model);
                return presets.Select(v => v.Id).Concat(Saved(key, model))
                    .Where(id => key == "bailian" || Fits(IdLocale(id), lang)).Distinct().ToList();
            case "xunfei":
                // 讯飞的音色要在控制台开通：只用正在用的和自己添加的，预置的不一定开通了
                return Saved("xunfei").Append(c.Str("xunfei_voice")).Where(id => id.Length > 0 && lang == "zh").Distinct().ToList();
            case "minimax":
                var mm = await ((MiniMax)eng).VoicesAsync(c);
                return mm.Select(v => v.Id).Where(id => Fits(IdLocale(id), lang)).ToList();
            default:
                return [];
        }
    }
}
