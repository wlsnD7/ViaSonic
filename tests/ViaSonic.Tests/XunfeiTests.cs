using System.Text;
using ViaSonic.Asr;
using ViaSonic.Engines;

namespace ViaSonic.Tests;

public class XunfeiTests
{
    /// <summary>讯飞文档「鉴权方法」里的示例：同样的 Key、Secret、时间要算出同样的签名。</summary>
    [Fact]
    public void SignatureMatchesOfficialExample()
    {
        var date = new DateTime(2019, 7, 10, 7, 35, 43, DateTimeKind.Utc);
        var url = XunfeiWs.SignedUrl("iat-api.xfyun.cn", "/v2/iat", "keyxxxxxxxx8ee279348519exxxxxxxx", "secretxxxxxxxx2df7900c09xxxxxxxx", date);
        Assert.StartsWith("wss://iat-api.xfyun.cn/v2/iat?authorization=", url);
        var query = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal("Wed, 10 Jul 2019 07:35:43 GMT", query["date"]);
        Assert.Equal("iat-api.xfyun.cn", query["host"]);
        var auth = Encoding.UTF8.GetString(Convert.FromBase64String(query["authorization"]));
        Assert.Equal("api_key=\"keyxxxxxxxx8ee279348519exxxxxxxx\", algorithm=\"hmac-sha256\", headers=\"host date request-line\", "
                     + "signature=\"Hp3Ty4ZkSBmL8jKyOLpQiv9Sr5nvmeYEH7WsL/ZO2Jg=\"", auth);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(-50, 0)]
    [InlineData(50, 100)]
    [InlineData(80, 100)]
    public void SpeedMapsToZeroToHundred(int rate, int speed) => Assert.Equal(speed, Xunfei.Speed(new JsonObject { ["xunfei_rate"] = rate }));

    [Fact]
    public void MissingSettingsInOrder()
    {
        var c = new JsonObject { ["xunfei_appid"] = "", ["xunfei_key"] = "", ["xunfei_secret"] = "" };
        Assert.Equal("请先填写 APPID", XunfeiWs.Missing(c));
        c["xunfei_appid"] = "abc";
        Assert.Equal("未填写 API Key", XunfeiWs.Missing(c));
        c["xunfei_key"] = Secrets.Protect("k");
        Assert.Equal("请先填写 API Secret", XunfeiWs.Missing(c));
        c["xunfei_secret"] = Secrets.Protect("s");
        Assert.Equal("", XunfeiWs.Missing(c));
        Assert.True(new Xunfei(() => c).Ready);
    }

    [Fact]
    public void NoLicenseErrorSaysAddVoice()
    {
        Assert.Equal("这个音色还没有开通，请先在讯飞控制台添加", XunfeiWs.Error(11200, "licc failed", "讯飞语音").Message);
        Assert.Equal("讯飞语音 请求失败：invalid text (10109)", XunfeiWs.Error(10109, "invalid text", "讯飞语音").Message);
    }

    [Fact]
    public void DictationWords()
    {
        var j = JsonNode.Parse("""
            {"code":0,"data":{"status":1,"result":{"sn":1,"ls":false,"ws":[
              {"bg":0,"cw":[{"sc":0,"w":"今天"}]},{"bg":0,"cw":[{"sc":0,"w":"天气"}]},{"bg":0,"cw":[{"sc":0,"w":"。"}]}]}}}
            """)!;
        Assert.Equal("今天天气。", XunfeiAsr.Words(j));
        Assert.Equal("", XunfeiAsr.Words(JsonNode.Parse("""{"code":0,"data":{"status":2}}""")!));
    }

    [Fact]
    public void DictationLanguageFollowsInterface()
    {
        Assert.Equal("zh_cn", XunfeiAsr.Lang(new JsonObject { ["stt_xunfei_lang"] = "" }));
        using (new LangScope("en")) Assert.Equal("en_us", XunfeiAsr.Lang(new JsonObject { ["stt_xunfei_lang"] = "" }));
        Assert.Equal("en_us", XunfeiAsr.Lang(new JsonObject { ["stt_xunfei_lang"] = "en_us" }));
    }
}
