using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ViaSonic;

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
        ["gsv_keep_loaded"] = true, // 切到别的引擎后 GPT-SoVITS 仍然保持加载
        ["gsv_speed"] = 1.0,
        ["gsv_text_lang"] = "zh",
        ["edge_voice"] = "zh-CN-YunxiNeural",
        ["edge_rate"] = 0,
        ["sapi_voice"] = "",
        ["sapi_rate"] = 0,
        // 联网服务：*_key 和 custom_headers 用 DPAPI 加密保存（Secrets）
        ["azure_key"] = "",
        ["azure_region"] = "",
        ["azure_lang"] = "zh-CN",
        ["azure_voice"] = "zh-CN-XiaoxiaoNeural",
        ["azure_style"] = "",
        ["azure_rate"] = 0,
        ["fish_key"] = "",
        ["fish_voice"] = "",
        ["fish_voices"] = new JsonArray(),    // 自己添加的音色 {id, name}；豆包、百炼还带 model
        ["fish_speed"] = 1.0,
        ["doubao_key"] = "",
        ["doubao_model"] = "2.0",
        ["doubao_voice"] = "zh_female_vv_uranus_bigtts",
        ["doubao_rate"] = 0,
        ["doubao_voices"] = new JsonArray(),
        ["bailian_key"] = "",
        ["bailian_region"] = "cn",
        ["bailian_model"] = "qwen",
        ["bailian_voice"] = "Cherry",
        ["bailian_speed"] = 1.0,
        ["bailian_voices"] = new JsonArray(),
        ["minimax_key"] = "",
        ["minimax_region"] = "cn",
        ["minimax_model"] = "speech-2.8-hd",
        ["minimax_voice"] = "male-qn-qingse",
        ["minimax_speed"] = 1.0,
        ["custom_method"] = "GET",
        ["custom_url"] = "",
        ["custom_headers"] = "",
        ["custom_body"] = "",
        ["mic_enabled"] = true,
        ["mic_device"] = "",
        ["mic_volume"] = 1.0,
        ["monitor_enabled"] = true,
        ["monitor_device"] = "",
        ["monitor_volume"] = 0.6,
        ["mic_passthrough"] = false,
        ["mic_monitor"] = false,     // 在监听设备里听到直通的麦克风（开着变声器时是变声后的声音）
        ["passthrough_device"] = "",
        ["passthrough_volume"] = 1.0,
        ["app_audio"] = false,
        ["app_audio_apps"] = new JsonArray(), // {exe, path, name, volume}
        ["sound_volume"] = 1.0,         // 音频文件进虚拟麦克风的音量
        ["sound_monitor_volume"] = 1.0, // 音频文件进监听的音量
        ["play_mode"] = "interrupt",
        ["random_voice"] = false,
        // 变声：fx_effects 是正在用的效果（调整后点「保存」才写进当前组合）
        ["fx_enabled"] = false,
        ["fx_mic"] = true,
        ["fx_speech"] = false,
        ["fx_effects"] = Audio.VoiceFx.DefaultEffects(),
        ["fx_presets"] = new JsonArray(), // {id, name, effects}
        ["fx_preset"] = "",
        ["rvc_home"] = "",
        ["rvc_models"] = new JsonArray(), // 自己添加的 .pth（完整路径）；整合包 assets\weights 里的自动列出
        ["rvc_f0"] = 0,                   // 0 RMVPE / 1 FCPE / 2 PM
        ["rvc_block_ms"] = 150,
        ["rvc_crossfade_ms"] = 50,
        ["rvc_extra_ms"] = 2500,
        ["lead_silence_ms"] = 120,
        ["ptt_enabled"] = true,
        ["ptt_key"] = "",
        ["ptt_delay_ms"] = 150,
        ["hotkeys"] = new JsonObject { ["input"] = "ctrl+alt+t", ["focus"] = "", ["stop"] = "ctrl+alt+x", ["repeat"] = "ctrl+alt+r", ["ptt"] = "", ["playlist"] = "", ["next"] = "" },
        ["hotkeys_paused"] = false,
        ["overlay"] = new JsonObject
        {
            ["opacity"] = 0.75, ["width_ratio"] = 0.22, ["x_ratio"] = 0.5, ["bottom_offset"] = 80, ["hide_after_send"] = false, ["restore_focus"] = false,
        },
        ["phrases"] = new JsonArray(NewPhrase()),
        ["playlist"] = new JsonArray(),      // {id, path}
        ["playlist_mode"] = "all",           // all 列表循环 / one 单曲循环 / shuffle 随机播放
        ["playlist_crossfade"] = 0,          // 交叉过渡秒数，0＝关
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

        var cfg = Normalize(raw, Lang.SystemDefault());
        Save(cfg);
        return cfg;
    }

    /// <summary>
    /// 读出来的配置（可能是旧版本的、手改坏的，或者 null＝全新安装）补齐默认值、做版本迁移。
    /// 类型和默认值对不上的项（比如该是列表的地方写成了 null）丢掉，用默认值。
    /// </summary>
    internal static JsonObject Normalize(JsonObject? raw, string systemLang)
    {
        var cfg = Defaults();
        if (raw != null)
        {
            foreach (var (k, v) in raw)
            {
                if (!cfg.TryGetPropertyValue(k, out var def) || !SameKind(def, v)) continue;
                if (def is JsonObject sub)
                {
                    foreach (var (sk, sv) in v!.AsObject())
                        if (!sub.TryGetPropertyValue(sk, out var sd) || SameKind(sd, sv)) sub[sk] = sv?.DeepClone();
                }
                else cfg[k] = v!.DeepClone();
            }
        }

        if (string.IsNullOrEmpty(cfg.Str("language")))
        {
            var lang = systemLang;
            cfg["language"] = lang;
            // 全新安装且是英文时，默认音色和合成语言也用英文；老配置升级只补语言，不动已有设置
            if (raw == null && lang == "en")
            {
                cfg["edge_voice"] = "en-US-GuyNeural";
                cfg["gsv_text_lang"] = "en";
                cfg["azure_lang"] = "en-US";
                cfg["azure_voice"] = "en-US-AvaMultilingualNeural";
                cfg["bailian_region"] = "intl";
                cfg["minimax_region"] = "intl";
            }
        }
        Lang.Init(cfg.Str("language"));
        if (cfg["gsv_models"] is not JsonArray) cfg["gsv_models"] = new JsonArray();
        var models = cfg["gsv_models"]!.AsArray();
        var cur = cfg["gsv_model"]?.GetValue<string>();
        if (!models.Any(m => m?["id"]?.GetValue<string>() == cur))
            cfg["gsv_model"] = models.Count > 0 ? models[0].Str("id") : "";
        MigrateVoices(cfg, raw);
        // 2.0 测试版的「监听麦克风」在变声页（fx_monitor），现在挪到音频页的麦克风直通下面
        if (raw?["fx_monitor"] is JsonValue fm && fm.TryGetValue<bool>(out var fmOn) && raw["mic_monitor"] == null) cfg["mic_monitor"] = fmOn;
        // 2.0 之前音频音量只有一个，监听的那个先用同样的值
        if (raw?["sound_volume"] is JsonValue soundVol && raw["sound_monitor_volume"] == null) cfg["sound_monitor_volume"] = soundVol.DeepClone();
        FixPresets(cfg);
        foreach (var p in cfg["phrases"]!.AsArray().OfType<JsonObject>())
        {
            // 1.0 的音频常用语没有名称：原来的文字当名称
            if (p["name"] == null && !string.IsNullOrEmpty(p["audio"]?.GetValue<string>())) p["name"] = p["text"]?.DeepClone();
            foreach (var key in new[] { "id", "text", "hotkey", "audio", "name" })
                p[key] ??= key == "id" ? NewId() : "";
            p["volume"] ??= 1.0;
            p["loop"] ??= false;
        }
        return cfg;
    }

    /// <summary>同一种 JSON 类型：对象、列表、字符串、数字、布尔（true/false 算一种）。</summary>
    private static bool SameKind(JsonNode? a, JsonNode? b)
    {
        if (a == null || b == null) return false;
        static JsonValueKind K(JsonNode n) => n.GetValueKind() is JsonValueKind.False ? JsonValueKind.True : n.GetValueKind();
        return K(a) == K(b);
    }

    public static JsonObject NewPreset(string name, JsonObject effects) => new()
    {
        ["id"] = NewId(), ["name"] = name, ["effects"] = effects.DeepClone(),
    };

    /// <summary>至少有一个变声组合（第一次用时建「默认」），当前组合要存在；缺的效果补上默认值。</summary>
    private static void FixPresets(JsonObject cfg)
    {
        var defaults = Audio.VoiceFx.DefaultEffects();
        void Fill(JsonObject effects)
        {
            foreach (var (k, v) in defaults)
            {
                if (effects[k] is not JsonObject e) { effects[k] = v!.DeepClone(); continue; }
                foreach (var (pk, pv) in v!.AsObject()) e[pk] ??= pv!.DeepClone();
            }
        }
        if (cfg["fx_effects"] is not JsonObject cur) cfg["fx_effects"] = cur = defaults.DeepClone().AsObject();
        Fill(cur);
        if (cfg["fx_presets"] is not JsonArray presets) cfg["fx_presets"] = presets = new JsonArray();
        if (presets.Count == 0) presets.Add((JsonNode)NewPreset(T("默认"), cur));
        foreach (var p in presets.OfType<JsonObject>())
        {
            p["id"] ??= NewId();
            p["name"] ??= "";
            if (p["effects"] is not JsonObject e) p["effects"] = e = defaults.DeepClone().AsObject();
            Fill(e);
        }
        var id = cfg.Str("fx_preset");
        if (!presets.Any(p => p?["id"]?.GetValue<string>() == id)) cfg["fx_preset"] = presets[0].Str("id");
    }

    /// <summary>1.3.0 只记当前音色：正在用的自定义音色 ID 放进添加的音色列表，切换后不会丢。</summary>
    private static void MigrateVoices(JsonObject cfg, JsonObject? raw)
    {
        foreach (var engine in new[] { "fish", "doubao", "bailian" })
        {
            if (cfg[engine + "_voices"] is not JsonArray) cfg[engine + "_voices"] = new JsonArray();
            if (raw?.ContainsKey(engine + "_voices") == true) continue; // 已经是新格式
            var id = cfg[engine + "_voice"]?.GetValue<string>() ?? "";
            if (id.Length == 0) continue;
            var model = engine == "fish" ? "" : cfg[engine + "_model"]?.GetValue<string>() ?? "";
            var presets = engine switch
            {
                "doubao" => Engines.Doubao.Presets(model),
                "bailian" => Engines.Bailian.Presets(model),
                _ => [],
            };
            if (presets.Any(v => v.Id == id)) continue;
            var v = new JsonObject { ["id"] = id, ["name"] = raw?[engine + "_voice_name"]?.GetValue<string>() is { Length: > 0 } n ? n : id };
            if (engine != "fish") v["model"] = model;
            cfg[engine + "_voices"]!.AsArray().Add((JsonNode)v);
        }
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
