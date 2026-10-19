namespace ViaSonic.Tests;

public class ConfigTests : IDisposable
{
    // Normalize 会按配置设置界面语言，每个测试后换回中文
    public void Dispose() => Lang.Init("zh");

    private static JsonObject Raw(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void FreshInstallGetsDefaults()
    {
        var c = ConfigStore.Normalize(null, "zh");
        Assert.Equal("zh", c.Str("language"));
        Assert.Equal("edge", c.Str("engine"));
        Assert.Equal("zh-CN-YunxiNeural", c.Str("edge_voice"));
        Assert.Single(c["phrases"]!.AsArray());
        var preset = Assert.Single(c["fx_presets"]!.AsArray());
        Assert.Equal("默认", preset.Str("name"));
        Assert.Equal(preset.Str("id"), c.Str("fx_preset"));
    }

    [Fact]
    public void FreshEnglishInstallUsesEnglishVoices()
    {
        var c = ConfigStore.Normalize(null, "en");
        Assert.Equal("en", c.Str("language"));
        Assert.Equal("en-US-GuyNeural", c.Str("edge_voice"));
        Assert.Equal("intl", c.Str("minimax_region"));
        Assert.Equal("Default", c["fx_presets"]![0].Str("name"));
    }

    [Fact]
    public void UpgradeOnlyFillsLanguage()
    {
        // 老版本没有 language：补上语言，但不动已有的音色设置
        var c = ConfigStore.Normalize(Raw("""{"edge_voice": "zh-CN-XiaoxiaoNeural"}"""), "en");
        Assert.Equal("en", c.Str("language"));
        Assert.Equal("zh-CN-XiaoxiaoNeural", c.Str("edge_voice"));
        Assert.Equal("cn", c.Str("minimax_region"));
    }

    [Fact]
    public void KeepsUserValuesAndDropsUnknownKeys()
    {
        var c = ConfigStore.Normalize(Raw("""{"engine": "azure", "mic_volume": 0.4, "ptt_enabled": false, "no_such_key": 1}"""), "zh");
        Assert.Equal("azure", c.Str("engine"));
        Assert.Equal(0.4, c.Num("mic_volume"));
        Assert.False(c.Bool("ptt_enabled", true));
        Assert.False(c.ContainsKey("no_such_key"));
    }

    [Fact]
    public void WrongTypesFallBackToDefaults()
    {
        // 手改坏的配置：该是列表的写成 null，该是对象的写成字符串，数字写成文字
        var c = ConfigStore.Normalize(Raw("""{"phrases": null, "hotkeys": "x", "mic_volume": "loud", "ptt_enabled": 1, "overlay": {"opacity": "half"}}"""), "zh");
        Assert.IsType<JsonArray>(c["phrases"]);
        Assert.Equal("ctrl+alt+t", c["hotkeys"].Str("input"));
        Assert.Equal(1.0, c.Num("mic_volume"));
        Assert.True(c.Bool("ptt_enabled"));
        Assert.Equal(0.75, c["overlay"].Num("opacity"));
    }

    [Fact]
    public void SubObjectsMergeIntoDefaults()
    {
        var c = ConfigStore.Normalize(Raw("""{"hotkeys": {"stop": "f9"}, "overlay": {"opacity": 0.5}}"""), "zh");
        Assert.Equal("f9", c["hotkeys"].Str("stop"));
        Assert.Equal("ctrl+alt+t", c["hotkeys"].Str("input"));
        Assert.Equal(0.5, c["overlay"].Num("opacity"));
        Assert.Equal(0.22, c["overlay"].Num("width_ratio"));
    }

    [Fact]
    public void MigratesMicMonitorFromVoicePage()
    {
        Assert.True(ConfigStore.Normalize(Raw("""{"fx_monitor": true}"""), "zh").Bool("mic_monitor"));
        // 已经有新设置时不覆盖
        Assert.False(ConfigStore.Normalize(Raw("""{"fx_monitor": true, "mic_monitor": false}"""), "zh").Bool("mic_monitor"));
    }

    [Fact]
    public void MonitorSoundVolumeStartsFromOldVolume()
    {
        Assert.Equal(0.3, ConfigStore.Normalize(Raw("""{"sound_volume": 0.3}"""), "zh").Num("sound_monitor_volume"));
        Assert.Equal(0.8, ConfigStore.Normalize(Raw("""{"sound_volume": 0.3, "sound_monitor_volume": 0.8}"""), "zh").Num("sound_monitor_volume"));
    }

    [Fact]
    public void PhrasesGetMissingFields()
    {
        var c = ConfigStore.Normalize(Raw("""{"phrases": [{"text": "你好"}, {"text": "爆炸", "audio": "boom.wav"}]}"""), "zh");
        var p = c["phrases"]!.AsArray();
        Assert.Equal(2, p.Count);
        Assert.NotEqual("", p[0].Str("id"));
        Assert.Equal("", p[0].Str("hotkey"));
        Assert.Equal(1.0, p[0].Num("volume"));
        Assert.False(p[0].Bool("loop", true));
        Assert.Equal("", p[0].Str("name"));
        Assert.Equal("爆炸", p[1].Str("name")); // 1.0 的音频常用语用原来的文字当名称
        Assert.NotEqual(p[0].Str("id"), p[1].Str("id"));
    }

    [Fact]
    public void CurrentGsvModelMustExist()
    {
        var c = ConfigStore.Normalize(Raw("""{"gsv_models": [{"id": "m1"}, {"id": "m2"}], "gsv_model": "gone"}"""), "zh");
        Assert.Equal("m1", c.Str("gsv_model"));
        Assert.Equal("m2", ConfigStore.Normalize(Raw("""{"gsv_models": [{"id": "m1"}, {"id": "m2"}], "gsv_model": "m2"}"""), "zh").Str("gsv_model"));
        Assert.Equal("", ConfigStore.Normalize(Raw("""{"gsv_model": "gone"}"""), "zh").Str("gsv_model"));
    }

    [Fact]
    public void CustomVoiceMovesIntoSavedList()
    {
        // 1.3.0 只记当前音色：自定义的音色放进添加的音色列表；预置音色不放
        var c = ConfigStore.Normalize(Raw("""{"fish_voice": "abc", "fish_voice_name": "我的声音", "bailian_voice": "Cherry"}"""), "zh");
        var fish = Assert.Single(c["fish_voices"]!.AsArray());
        Assert.Equal("abc", fish.Str("id"));
        Assert.Equal("我的声音", fish.Str("name"));
        Assert.Empty(c["bailian_voices"]!.AsArray());
        // 已经是新格式就不再迁移
        Assert.Empty(ConfigStore.Normalize(Raw("""{"fish_voice": "abc", "fish_voices": []}"""), "zh")["fish_voices"]!.AsArray());
    }

    [Fact]
    public void PresetsAreRepaired()
    {
        var c = ConfigStore.Normalize(Raw("""{"fx_presets": [{"id": "p1", "name": "A", "effects": {"pitch": {"on": true}}}], "fx_preset": "gone"}"""), "zh");
        Assert.Equal("p1", c.Str("fx_preset"));
        var effects = c["fx_presets"]![0]!["effects"]!;
        Assert.True(effects["pitch"].Bool("on"));
        Assert.Equal(0, effects["pitch"].Num("semitones")); // 缺的参数补默认值
        foreach (var def in Audio.VoiceFx.Defs) Assert.IsType<JsonObject>(effects[def.Key]);
        Assert.IsType<JsonObject>(effects["rvc"]);
    }

    [Fact]
    public void NormalizeIsStable()
    {
        var once = ConfigStore.Normalize(null, "zh");
        var twice = ConfigStore.Normalize(once.DeepClone().AsObject(), "zh");
        Assert.Equal(once.ToJsonString(), twice.ToJsonString());
    }
}
