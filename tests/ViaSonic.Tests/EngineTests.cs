using System.Net;
using ViaSonic.Audio;
using ViaSonic.Engines;

namespace ViaSonic.Tests;

public class OnlineEngineTests : IDisposable
{
    public void Dispose() => Lang.Init("zh");

    private sealed class Probe(JsonObject cfg) : OnlineEngine(() => cfg)
    {
        public override string Key => "probe";
        public override string Label => "Probe";
        public override string CacheKey(JsonObject c) => "";
        public override Task<Clip> SynthAsync(string text, JsonObject c, CancellationToken token) => throw new NotSupportedException();

        public static string ErrorOf(string body) => ErrorText(body);
        public TtsException Failure(HttpStatusCode s, string body) => Fail(s, body);
        public static bool Audio(byte[] d) => IsAudio(d);
    }

    [Fact]
    public void NotReadyWithoutKey()
    {
        var cfg = new JsonObject { ["probe_key"] = "" };
        var e = new Probe(cfg);
        Assert.False(e.Ready);
        Assert.Equal("请先填写 Probe 的 API Key", e.Error);
        cfg["probe_key"] = Secrets.Protect("sk-test");
        Assert.True(e.Ready);
        Assert.Equal("", e.Error);
    }

    [Theory]
    [InlineData("""{"message": "quota exceeded"}""", "quota exceeded")]
    [InlineData("""{"base_resp": {"status_code": 1, "status_msg": "bad voice"}}""", "bad voice")]
    [InlineData("""{"error": {"message": "nested"}}""", "nested")]
    [InlineData("""{"header": {"message": "doubao"}}""", "doubao")]
    [InlineData("""{"detail": {"status": "voice_not_found", "message": "elevenlabs"}}""", "elevenlabs")]
    [InlineData("plain text error", "plain text error")]
    public void FindsErrorText(string body, string expected) => Assert.Equal(expected, Probe.ErrorOf(body));

    [Fact]
    public void LongBodyIsCut() => Assert.Equal(200, Probe.ErrorOf(new string('x', 1000)).Length);

    [Fact]
    public void StatusCodesBecomeReadableErrors()
    {
        var e = new Probe(new JsonObject());
        Assert.Equal("API Key 无效或已过期", e.Failure(HttpStatusCode.Unauthorized, "").Message);
        Assert.Equal("API Key 无效或已过期", e.Failure(HttpStatusCode.Forbidden, "").Message);
        // 豆包的模型没开通也是 403
        Assert.Equal("这个模型还没有开通，请先在控制台开通", e.Failure(HttpStatusCode.Forbidden, """{"code": 45000030}""").Message);
        Assert.Equal("Probe 请求失败：HTTP 500", e.Failure(HttpStatusCode.InternalServerError, "").Message);
        Assert.Equal("Probe 请求失败：busy", e.Failure(HttpStatusCode.TooManyRequests, """{"detail": "busy"}""").Message);
        // ElevenLabs 额度用完也是 401，不能说成 Key 无效
        Assert.Equal("Probe 请求失败：out of credits",
            e.Failure(HttpStatusCode.Unauthorized, """{"detail": {"status": "quota_exceeded", "message": "out of credits"}}""").Message);
        // Key 能用，只是缺某项权限
        Assert.Equal("Probe 请求失败：missing tts",
            e.Failure(HttpStatusCode.Unauthorized, """{"detail": {"status": "missing_permissions", "message": "missing tts"}}""").Message);
    }

    [Fact]
    public void RecognizesAudioHeaders()
    {
        Assert.True(Probe.Audio("RIFF\0\0\0\0WAVEfmt "u8.ToArray()));
        Assert.True(Probe.Audio("ID3\x03\0\0"u8.ToArray()));
        Assert.True(Probe.Audio([0xFF, 0xFB, 0x90, 0x00, 0x00]));
        Assert.True(Probe.Audio("OggS\0\0"u8.ToArray()));
        Assert.True(Probe.Audio("fLaC\0\0"u8.ToArray()));
        Assert.False(Probe.Audio("""{"error": 1}"""u8.ToArray()));
        Assert.False(Probe.Audio([]));
    }

    [Fact]
    public void CustomApiNeedsUrlNotKey()
    {
        var cfg = ConfigStore.Normalize(null, "zh");
        var e = new CustomApi(() => cfg);
        Assert.False(e.Ready);
        Assert.Equal("还没有填写地址", e.Missing(cfg));
        cfg["custom_url"] = "http://127.0.0.1:9880/tts?text={text}";
        Assert.True(e.Ready);
    }

    public static IEnumerable<object[]> VoiceEngines() =>
        new[] { "azure", "fish", "doubao", "bailian", "minimax" }.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(VoiceEngines))]
    public void CacheKeyChangesWithVoice(string key)
    {
        var cfg = ConfigStore.Normalize(null, "zh");
        var engines = new Dictionary<string, ITtsEngine>
        {
            ["azure"] = new AzureTts(() => cfg), ["fish"] = new FishAudio(() => cfg), ["doubao"] = new Doubao(() => cfg),
            ["bailian"] = new Bailian(() => cfg), ["minimax"] = new MiniMax(() => cfg),
        };
        var before = engines[key].CacheKey(cfg);
        cfg[key + "_voice"] = "another-voice";
        Assert.NotEqual(before, engines[key].CacheKey(cfg));
    }
}

public class SecretsTests
{
    [Fact]
    public void RoundTrip()
    {
        var stored = Secrets.Protect("sk-测试-123");
        Assert.StartsWith("dpapi:", stored);
        Assert.DoesNotContain("sk-", stored);
        Assert.Equal("sk-测试-123", Secrets.Get(new JsonObject { ["k"] = stored }, "k"));
    }

    [Fact]
    public void PlainAndEmptyValues()
    {
        Assert.Equal("", Secrets.Protect(""));
        Assert.Equal("plain", Secrets.Get(new JsonObject { ["k"] = "plain" }, "k")); // 老配置里没加密的
        Assert.Equal("", Secrets.Get(new JsonObject(), "k"));
    }
}

public class RandomVoiceTests
{
    [Theory]
    [InlineData("你好", "zh")]
    [InlineData("hello", "en")]
    [InlineData("こんにちは", "ja")]
    [InlineData("日本語のテキスト", "ja")]   // 有假名就算日语
    [InlineData("안녕하세요", "ko")]
    [InlineData("OK 好的", "zh")]
    public void DetectsLanguage(string text, string lang) => Assert.Equal(lang, RandomVoice.TextLang(text));

    private sealed class Fake(string key, bool ready) : ITtsEngine
    {
        public string Key => key;
        public string Label => key;
        public bool Ready => ready;
        public bool Loading => false;
        public string Error => "";
        public string ErrorKind => "";
        public string CacheKey(JsonObject cfg) => key;
        public void Load(JsonObject cfg) { }
        public Task<Clip> SynthAsync(string text, JsonObject cfg, CancellationToken token) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("hello there", "en")]
    [InlineData("こんにちは", "ja")]
    public async Task PicksVoiceThatCanReadText(string text, string lang)
    {
        var cfg = ConfigStore.Normalize(null, "zh");
        Lang.Init("zh");
        var engines = new Dictionary<string, ITtsEngine> { ["edge"] = new EdgeTts(), ["gsv"] = new Fake("gsv", false) };
        var pick = (await RandomVoice.PickAsync(cfg, text, engines));
        Assert.NotNull(pick);
        Assert.Equal("edge", pick.Value.Cfg.Str("engine"));
        Assert.Equal(lang, EdgeVoices.LangOf(pick.Value.Cfg.Str("edge_voice")));
        Assert.Equal("zh-CN-YunxiNeural", cfg.Str("edge_voice")); // 原配置不动
    }

    [Fact]
    public async Task ChineseTextGetsChineseVoice()
    {
        var cfg = ConfigStore.Normalize(null, "zh");
        Lang.Init("zh");
        var engines = new Dictionary<string, ITtsEngine> { ["edge"] = new EdgeTts() };
        for (int i = 0; i < 10; i++)
            Assert.StartsWith("zh-", (await RandomVoice.PickAsync(cfg, "今天天气不错", engines))!.Value.Cfg.Str("edge_voice"));
    }

    [Fact]
    public async Task NothingReadyMeansNoPick()
    {
        var engines = new Dictionary<string, ITtsEngine> { ["gsv"] = new Fake("gsv", false) };
        Assert.Null((await RandomVoice.PickAsync(new JsonObject(), "你好", engines)));
    }
}

public class AudioFileTests
{
    [Fact]
    public void WavRoundTrip()
    {
        var clip = Signal.Sine(440, 0.25, 22050);
        var path = Path.Combine(Path.GetTempPath(), $"viasonic-test-{Guid.NewGuid():N}.wav");
        try
        {
            AudioEncode.Save(clip, path);
            var back = AudioDecode.FromFile(path);
            Assert.Equal(clip.SampleRate, back.SampleRate);
            Assert.Equal(clip.Samples.Length, back.Samples.Length);
            for (int i = 0; i < clip.Samples.Length; i += 97) Assert.InRange(back.Samples[i] - clip.Samples[i], -1e-4f, 1e-4f); // 16 位量化误差
            Assert.Equal(0.25, AudioDecode.Duration(path).TotalSeconds, 2);
            Assert.Equal(back.Samples, AudioDecode.FromWave(File.ReadAllBytes(path)).Samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LeadSilence()
    {
        var clip = new Clip([1f, 1f], 1000);
        var padded = clip.WithLeadSilence(5);
        Assert.Equal([0f, 0f, 0f, 0f, 0f, 1f, 1f], padded.Samples);
        Assert.Same(clip, clip.WithLeadSilence(0));
    }
}
