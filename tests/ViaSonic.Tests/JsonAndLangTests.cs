namespace ViaSonic.Tests;

public class JsonTests
{
    private static readonly JsonObject O = JsonNode.Parse("""{"s": "x", "b": true, "i": 3, "d": 0.5, "n": null, "o": {"k": "v"}}""")!.AsObject();

    [Fact]
    public void ReadsFieldsOfTheRightType()
    {
        Assert.Equal("x", O.Str("s"));
        Assert.True(O.Bool("b"));
        Assert.Equal(3, O.Num("i"));
        Assert.Equal(0.5, O.Num("d"));
        Assert.Equal("v", O["o"].Str("k"));
    }

    [Fact]
    public void MissingOrWrongTypeGivesDefault()
    {
        Assert.Equal("", O.Str("missing"));
        Assert.Equal("def", O.Str("missing", "def"));
        Assert.Equal("", O.Str("i"));       // 数字不是字符串
        Assert.Equal("", O.Str("n"));
        Assert.False(O.Bool("s"));
        Assert.True(O.Bool("missing", true));
        Assert.Equal(0, O.Num("s"));
        Assert.Equal(0, O.Num("o"));
        Assert.Equal("", O["o"].Str("s"));
    }

    [Fact]
    public void SafeOnNullAndNonObjects()
    {
        JsonNode? none = null;
        Assert.Equal("", none.Str("a"));
        Assert.False(none.Bool("a"));
        Assert.Equal(0, none.Num("a"));
        Assert.Equal("", new JsonArray(1, 2).Str("a"));
        Assert.Equal("", JsonValue.Create("text").Str("a"));
    }
}

public class LangTests
{
    [Fact]
    public void ChineseIsTheKey()
    {
        using var _ = new LangScope("zh");
        Assert.Equal("设置", Lang.T("设置"));
        Assert.Equal("发现新版本 v2", Lang.T("发现新版本 {0}", "v2"));
    }

    [Fact]
    public void EnglishLooksUpTableAndFallsBack()
    {
        using var _ = new LangScope("en");
        Assert.Equal("Settings", Lang.T("设置"));
        Assert.Equal("没有翻译的文字", Lang.T("没有翻译的文字"));
    }

    [Fact]
    public void SetRaisesChangedOnlyOnChange()
    {
        using var _ = new LangScope("zh");
        int n = 0;
        void OnChanged() => n++;
        Lang.Changed += OnChanged;
        try
        {
            Lang.Set("zh");
            Lang.Set("en");
            Lang.Set("en");
            Lang.Set("fr"); // 只有中英文，其他算中文
            Assert.Equal(2, n);
            Assert.Equal("zh", Lang.Current);
        }
        finally
        {
            Lang.Changed -= OnChanged;
        }
    }
}
