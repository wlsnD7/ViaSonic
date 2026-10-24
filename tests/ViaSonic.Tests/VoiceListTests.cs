using ViaSonic.Engines;

namespace ViaSonic.Tests;

public class EdgeVoiceTests
{
    [Fact]
    public void AllVoicesAreListedOnce()
    {
        var ids = EdgeVoices.Ids.ToList();
        Assert.Equal(322, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.EndsWith("Neural", id));
    }

    [Fact]
    public void DefaultVoiceExists() =>
        Assert.Contains(ConfigStore.Normalize(new JsonObject(), "zh").Str("edge_voice"), EdgeVoices.Ids);

    [Fact]
    public void EveryVoiceBelongsToAListedLanguage()
    {
        var langs = EdgeVoices.Langs().Select(l => l.Id).ToList();
        Assert.Equal(langs.Count, langs.Distinct().Count());
        foreach (var id in EdgeVoices.Ids) Assert.Contains(EdgeVoices.LangOf(id), langs);
        // 每种语言都有音色，合起来正好是全部
        Assert.All(langs, l => Assert.NotEmpty(EdgeVoices.Voices(l)));
        Assert.Equal(EdgeVoices.Ids.Count(), langs.Sum(l => EdgeVoices.Voices(l).Count));
    }

    [Fact]
    public void FixedLanguagesComeFirst() =>
        Assert.Equal(["zh", "yue", "en", "ja", "ko"], EdgeVoices.Langs().Take(5).Select(l => l.Id));

    [Fact]
    public void CantoneseIsItsOwnLanguage()
    {
        Assert.Equal("yue", EdgeVoices.LangOf("zh-HK-HiuGaaiNeural"));
        Assert.Equal("zh", EdgeVoices.LangOf("zh-CN-liaoning-XiaobeiNeural"));
        Assert.Equal("iu", EdgeVoices.LangOf("iu-Latn-CA-SiqiniqNeural"));
        Assert.Equal(11, EdgeVoices.Voices("zh").Count);
        Assert.Equal(3, EdgeVoices.Voices("yue").Count);
    }

    [Fact]
    public void LabelsAreJustNames()
    {
        using (new LangScope("zh"))
        {
            var en = EdgeVoices.Voices("en");
            Assert.StartsWith("en-US-", en[0].Id); // 主要地区排前面
            Assert.Contains(("en-US-AvaMultilingualNeural", "Ava 多语种"), en);
            Assert.Contains(("en-US-AvaNeural", "Ava"), en);
            Assert.Contains(("en-GB-LibbyNeural", "Libby"), en);
            Assert.Contains(("ja-JP-KeitaNeural", "Keita"), EdgeVoices.Voices("ja"));
            Assert.Contains(("zh-HK-HiuGaaiNeural", "曉佳"), EdgeVoices.Voices("yue"));
            Assert.Equal("云希", EdgeVoices.Voices("zh")[0].Name);
            // 只有重名时才标区别
            Assert.Contains(("iu-Cans-CA-SiqiniqNeural", "Siqiniq（音节文字）"), EdgeVoices.Voices("iu"));
        }
        using (new LangScope("en"))
        {
            Assert.Contains(("en-US-AvaMultilingualNeural", "Ava Multilingual"), EdgeVoices.Voices("en"));
            Assert.Contains(("zh-HK-HiuGaaiNeural", "HiuGaai"), EdgeVoices.Voices("yue"));
            Assert.Contains(("iu-Latn-CA-SiqiniqNeural", "Siqiniq (Latin)"), EdgeVoices.Voices("iu"));
            Assert.Equal("Japanese", EdgeVoices.Langs()[3].Name);
        }
    }
}

public class EdgeTokenTests
{
    // 和 rany2/edge-tts 的 generate_sec_ms_gec_v2 算出的值对照
    [Theory]
    [InlineData(0, "52BC7A17B50DFEC9-28C4394775C8751CD2F6859C030C06DB1489ACAB3B3A7651831FC26895177ED6")]
    [InlineData(1700000000, "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2")]
    [InlineData(1792830267.5, "A489738B0C1986BF-66CCAE725DFACDE19BD1D765CCDE8533A63452533B7EE9BF08491808D644D260")]
    [InlineData(1800000123, "99E54519B490BE5C-E162DB0977FBDCB9B86DD8148BF1BCDC3C0303176CE39852A42B415935C20654")]
    public void MatchesReference(double unix, string expected) =>
        Assert.Equal(expected, EdgeToken.Make(unix, "6A5AA1D4EAFF4E9FB37E23D68491D6F4"));

    [Fact]
    public void ChangesEveryFiveMinutes()
    {
        // 1800000000 正好在 5 分钟边界上（Windows 和 Unix 纪元相差的秒数是 300 的倍数）
        Assert.Equal(EdgeToken.Make(1800000000, "x"), EdgeToken.Make(1800000299.5, "x"));
        Assert.NotEqual(EdgeToken.Make(1800000000, "x"), EdgeToken.Make(1800000300, "x"));
    }
}

public class ElevenLabsTests
{
    [Theory]
    [InlineData("Roger - Laid-Back, Casual, Resonant", "premade", "Roger")]
    [InlineData("George", "premade", "George")]
    [InlineData("My - Clone", "cloned", "My - Clone")] // 自己的音色名不动
    public void PremadeNamesDropTheDescription(string name, string category, string expected) =>
        Assert.Equal(expected, ElevenLabs.VoiceName(new JsonObject { ["name"] = name, ["category"] = category }));
}

public class KokoroTests
{
    [Fact]
    public void VoicesCoverEverySpeakerOnce()
    {
        var ids = KokoroTts.Voices().Select(v => int.Parse(v.Id)).ToList();
        Assert.Equal(Enumerable.Range(0, 103), ids.Order());
        Assert.Equal(KokoroTts.DefaultVoice, KokoroTts.Voices()[0].Id);
        Assert.Equal(KokoroTts.DefaultVoice, ConfigStore.Normalize(new JsonObject(), "zh").Str("kokoro_voice"));
    }

    [Fact]
    public void VoiceNames()
    {
        var v = KokoroTts.Voices();
        Assert.Equal("女声 01", v[0].Name);
        Assert.Equal("女声 55", v[54].Name);
        Assert.Equal(("58", "男声 01"), v[55]);
        Assert.Equal(("2", "英式女声"), v[^1]);
    }

    [Theory]
    [InlineData("model.int8.onnx", true)]
    [InlineData("voices.bin", true)]
    [InlineData("lexicon-zh.txt", true)]
    [InlineData("number-zh.fst", true)]
    [InlineData("espeak-ng-data/phontab", true)]
    [InlineData("espeak-ng-data/en_dict", true)]
    [InlineData("espeak-ng-data/lang/gmw/en-US", true)]
    [InlineData("espeak-ng-data/ru_dict", false)]
    [InlineData("espeak-ng-data/voices/!v/f1", false)]
    [InlineData("lexicon-gb-en.txt", false)]
    [InlineData("dict/jieba.dict.utf8", false)]
    [InlineData("README.md", false)]
    [InlineData(".gitattributes", false)]
    public void DownloadsOnlyWhatIsUsed(string path, bool needed) => Assert.Equal(needed, KokoroModel.Needed(path));
}
