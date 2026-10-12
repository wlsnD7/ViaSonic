using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViaTTS;

/// <summary>config.json 读写与默认值。</summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject NewPhrase() => new()
    {
        ["id"] = NewId(),
        ["text"] = "",
        ["hotkey"] = "",
        ["audio"] = "",
        ["name"] = "",
        ["volume"] = 1.0,
        ["loop"] = false,
    };

    public static JsonObject NewModel(string? name = null) => new()
    {
        ["id"] = NewId(),
        ["name"] = name ?? T("新模型"),
        ["gpt"] = "", ["sovits"] = "", ["ref"] = "", ["ref_text"] = "", ["ref_lang"] = "zh",
    };

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];


    private static JsonObject Defaults() => new()
    {
        ["engine"] = "edge",
        ["gsv_home"] = "",
        ["gsv_models"] = new JsonArray(),   // 模型由用户自己添加
        ["gsv_model"] = "",
        ["gsv_device"] = "cuda",
        ["gsv_speed"] = 1.0,
        ["gsv_text_lang"] = "zh",
        ["edge_voice"] = "zh-CN-YunxiNeural",
        ["edge_rate"] = 0,
        ["sapi_voice"] = "",
        ["sapi_rate"] = 0,
        ["mic_enabled"] = true,
        ["mic_device"] = "",
        ["mic_volume"] = 1.0,
        ["monitor_enabled"] = true,
        ["monitor_device"] = "",
        ["monitor_volume"] = 0.6,
        ["mic_passthrough"] = false,
        ["passthrough_device"] = "",
        ["passthrough_volume"] = 1.0,
        ["sound_volume"] = 1.0,
        ["play_mode"] = "interrupt",
        ["lead_silence_ms"] = 120,
        ["ptt_key"] = "",
        ["ptt_delay_ms"] = 150,
        ["hotkeys"] = new JsonObject { ["input"] = "ctrl+alt+t", ["focus"] = "", ["stop"] = "ctrl+alt+x", ["repeat"] = "ctrl+alt+r" },
        ["hotkeys_paused"] = false,
        ["overlay"] = new JsonObject
        {
            ["opacity"] = 0.75, ["width_ratio"] = 0.22, ["bottom_offset"] = 80, ["hide_after_send"] = false, ["restore_focus"] = false,
        },
        ["phrases"] = new JsonArray(NewPhrase()),
        ["welcome_shown"] = false,
        ["language"] = "",
    };

    public static JsonObject Load()
    {
        JsonObject? raw = null;
        var path = AppPaths.ConfigFile;
        if (File.Exists(path))
        {
            try
            {
                raw = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            }
            catch (Exception e)
            {
                Log.Warn(T("配置文件损坏，已重置：{0}（{1}）", path, e.Message));
                try { File.Move(path, path + ".broken", true); } catch { }
            }
        }

        var cfg = Defaults();
        if (raw != null)
        {
            foreach (var (k, v) in raw)
            {
                if (!cfg.ContainsKey(k)) continue;
                if (v is JsonObject sub && cfg[k] is JsonObject def)
                    foreach (var (sk, sv) in sub) def[sk] = sv?.DeepClone();
                else cfg[k] = v?.DeepClone();
            }
        }

        if (string.IsNullOrEmpty(cfg["language"]?.GetValue<string>()))
        {
            var lang = Lang.SystemDefault();
            cfg["language"] = lang;
            // 全新安装且是英文时，默认音色和合成语言也用英文；老配置升级只补语言，不动已有设置
            if (raw == null && lang == "en")
            {
                cfg["edge_voice"] = "en-US-GuyNeural";
                cfg["gsv_text_lang"] = "en";
            }
        }
        Lang.Init(cfg["language"]!.GetValue<string>());
        if (cfg["gsv_models"] is not JsonArray) cfg["gsv_models"] = new JsonArray();
        var models = cfg["gsv_models"]!.AsArray();
        var cur = cfg["gsv_model"]?.GetValue<string>();
        if (!models.Any(m => m?["id"]?.GetValue<string>() == cur))
            cfg["gsv_model"] = models.Count > 0 ? models[0]!["id"]!.GetValue<string>() : "";
        foreach (var p in cfg["phrases"]!.AsArray().OfType<JsonObject>())
        {
            // 1.0 的音频常用语没有名称：原来的文字当名称
            if (p["name"] == null && !string.IsNullOrEmpty(p["audio"]?.GetValue<string>())) p["name"] = p["text"]?.DeepClone();
            foreach (var key in new[] { "id", "text", "hotkey", "audio", "name" })
                p[key] ??= key == "id" ? NewId() : "";
            p["volume"] ??= 1.0;
            p["loop"] ??= false;
        }

        Save(cfg);
        return cfg;
    }

    public static void Save(JsonObject cfg)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, cfg.ToJsonString(WriteOptions));
            File.Move(tmp, AppPaths.ConfigFile, true);
        }
        catch (Exception e)
        {
            Log.Error(T("保存配置失败"), e);
        }
    }
}
