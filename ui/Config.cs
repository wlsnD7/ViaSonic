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
    };

    public static JsonObject NewModel(string name = "新模型") => new()
    {
        ["id"] = NewId(),
        ["name"] = name,
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
        ["play_mode"] = "interrupt",
        ["lead_silence_ms"] = 120,
        ["ptt_key"] = "",
        ["ptt_delay_ms"] = 150,
        ["hotkeys"] = new JsonObject { ["input"] = "ctrl+alt+t", ["focus"] = "", ["stop"] = "ctrl+alt+x", ["repeat"] = "ctrl+alt+r" },
        ["hotkeys_paused"] = false,
        ["overlay"] = new JsonObject
        {
            ["opacity"] = 0.8, ["width_ratio"] = 0.22, ["bottom_offset"] = 160, ["hide_after_send"] = true, ["restore_focus"] = false,
        },
        ["phrases"] = new JsonArray(NewPhrase()),
        ["welcome_shown"] = false,
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
                Log.Warn($"配置文件损坏，已重置：{path}（{e.Message}）");
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

        if (cfg["gsv_models"] is not JsonArray) cfg["gsv_models"] = new JsonArray();
        var models = cfg["gsv_models"]!.AsArray();
        var cur = cfg["gsv_model"]?.GetValue<string>();
        if (!models.Any(m => m?["id"]?.GetValue<string>() == cur))
            cfg["gsv_model"] = models.Count > 0 ? models[0]!["id"]!.GetValue<string>() : "";
        foreach (var p in cfg["phrases"]!.AsArray().OfType<JsonObject>())
            foreach (var key in new[] { "id", "text", "hotkey", "audio" })
                p[key] ??= key == "id" ? NewId() : "";

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
            Log.Error("保存配置失败", e);
        }
    }
}
