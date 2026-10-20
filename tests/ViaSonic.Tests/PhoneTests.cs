using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ViaSonic.Phone;

namespace ViaSonic.Tests;

/// <summary>手机输入的网页服务：在本机随便一个端口上启动，用假的朗读接口检查。</summary>
public class PhoneTests : IDisposable
{
    private sealed class FakeHost : IPhoneHost
    {
        public string Token { get; set; } = "abcdefghijklmnop";
        public List<string> Said { get; } = [];
        public int Stops, Repeats;
        public List<string> Played { get; } = [];
        public List<PhoneItem> Items { get; } = [new("a1", "你好", false), new("b2", "背景音乐", true)];

        /// <summary>每句朗读的结果由测试决定什么时候给。</summary>
        public List<TaskCompletionSource<bool>> Speaking { get; } = [];
        public Task<bool> SayAsync(string text)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Said)
            {
                Said.Add(text);
                Speaking.Add(tcs);
            }
            return tcs.Task;
        }
        public void Stop() => Stops++;
        public void Repeat() => Repeats++;
        public Task<bool> PlayAsync(string id)
        {
            Played.Add(id);
            return Task.FromResult(Items.Any(i => i.Id == id));
        }
        public Task<IReadOnlyList<PhoneItem>> PhrasesAsync() => Task.FromResult<IReadOnlyList<PhoneItem>>(Items);
    }

    private readonly FakeHost _host = new();
    private readonly PhoneServer _server;
    private readonly HttpClient _http;

    public PhoneTests()
    {
        _server = new PhoneServer(_host, 0, IPAddress.Loopback);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}") };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    private Task<HttpResponseMessage> Post(string path, string body = "") =>
        _http.PostAsync(path, new StringContent(body, Encoding.UTF8, "text/plain"));

    [Fact]
    public async Task PageHasTokenOnlyWhenLinkIsValid()
    {
        var good = await _http.GetStringAsync("/?k=abcdefghijklmnop");
        Assert.Contains("manifest.webmanifest?k=abcdefghijklmnop", good);
        Assert.Contains("\"k\":\"abcdefghijklmnop\"", good);
        Assert.DoesNotContain("__DATA__", good);

        var bad = await _http.GetStringAsync("/?k=wrong");
        Assert.Contains("\"k\":null", bad);
        Assert.DoesNotContain("abcdefghijklmnop", bad);
    }

    [Fact]
    public async Task PageTextFollowsLanguage()
    {
        using (new LangScope("en"))
            Assert.Contains("Type text", await _http.GetStringAsync("/?k=abcdefghijklmnop"));
        var zh = await _http.GetStringAsync("/?k=abcdefghijklmnop");
        Assert.Contains("lang=\"zh-CN\"", zh);
        Assert.Contains("\\u", zh); // 中文被转义成 \uXXXX，放进 <script> 也安全
    }

    [Fact]
    public async Task ApiNeedsToken()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/say", "你好")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/say?k=abcdefghijklmnoq", "你好")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync("/api/phrases")).StatusCode);
        Assert.Empty(_host.Said);

        // 更换链接后旧令牌立即失效
        _host.Token = "ponmlkjihgfedcba";
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/stop?k=abcdefghijklmnop")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/stop?k=ponmlkjihgfedcba")).StatusCode);
    }

    [Fact]
    public async Task SayStopRepeat()
    {
        Assert.Equal(HttpStatusCode.OK, (await Post("/api/say?k=abcdefghijklmnop", "  你好\n世界\u0007 ")).StatusCode);
        Assert.Equal(["你好 世界"], _host.Said);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/say?k=abcdefghijklmnop", "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
            (await Post("/api/say?k=abcdefghijklmnop", new string('字', PhoneServer.MaxText + 1))).StatusCode);
        await Post("/api/stop?k=abcdefghijklmnop");
        await Post("/api/repeat?k=abcdefghijklmnop");
        Assert.Equal(1, _host.Stops);
        Assert.Equal(1, _host.Repeats);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _http.GetAsync("/api/say?k=abcdefghijklmnop")).StatusCode);
    }

    [Fact]
    public async Task WaitReturnsWhenSpeechEnds()
    {
        async Task<long> Say(string text) =>
            JsonNode.Parse(await (await Post("/api/say?k=abcdefghijklmnop", text)).Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
        var first = await Say("第一句");
        var second = await Say("第二句");
        Assert.NotEqual(first, second);

        var wait = _http.GetAsync($"/api/wait?k=abcdefghijklmnop&id={first}");
        await Task.Delay(200);
        Assert.False(wait.IsCompleted); // 还在读
        _host.Speaking[0].SetResult(true);
        Assert.Equal(HttpStatusCode.NoContent, (await wait).StatusCode);

        // 合成失败：500，手机保留文字
        _host.Speaking[1].SetResult(false);
        Assert.Equal(HttpStatusCode.InternalServerError, (await _http.GetAsync($"/api/wait?k=abcdefghijklmnop&id={second}")).StatusCode);
        // 不认识的编号当作读完了
        Assert.Equal(HttpStatusCode.NoContent, (await _http.GetAsync("/api/wait?k=abcdefghijklmnop&id=999")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync($"/api/wait?id={first}")).StatusCode);
    }

    [Fact]
    public async Task PhrasesAndPlay()
    {
        var list = JsonNode.Parse(await _http.GetStringAsync("/api/phrases?k=abcdefghijklmnop"))!.AsArray();
        Assert.Equal(2, list.Count);
        Assert.Equal("你好", list[0].Str("label"));
        Assert.True(list[1].Bool("loop"));

        Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/play?k=abcdefghijklmnop&id=a1")).StatusCode);
        // 电脑上已经删掉的条目：404，手机重新读取列表
        Assert.Equal(HttpStatusCode.NotFound, (await Post("/api/play?k=abcdefghijklmnop&id=zz")).StatusCode);
        Assert.Equal(["a1", "zz"], _host.Played);
    }

    [Fact]
    public async Task TooManyRequestsAreRejected()
    {
        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 20; i++) codes.Add((await Post("/api/stop?k=abcdefghijklmnop")).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.True(codes.Count(c => c == HttpStatusCode.NoContent) >= 10);
    }

    [Fact]
    public async Task BadRequestsDontCrash()
    {
        foreach (var raw in new[] { "garbage\r\n\r\n", "POST /api/say HTTP/1.1\r\nContent-Length: 99999\r\n\r\n", new string('a', 20000) })
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, _server.Port);
            var s = c.GetStream();
            try
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes(raw));
                var buf = new byte[256];
                var n = await s.ReadAsync(buf);
                if (n > 0) Assert.StartsWith("HTTP/1.1 400", Encoding.ASCII.GetString(buf, 0, n));
            }
            catch (IOException) { } // 太长的请求，服务器可能直接断开
        }
        Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/stop?k=abcdefghijklmnop")).StatusCode);
    }

    [Fact]
    public void PortInUseThrows()
    {
        var e = Assert.Throws<SocketException>(() => new PhoneServer(_host, _server.Port, IPAddress.Loopback));
        Assert.Equal(SocketError.AddressAlreadyInUse, e.SocketErrorCode);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:4860::8888", false)]
    public void OnlyLocalNetwork(string ip, bool allowed) => Assert.Equal(allowed, PhoneServer.Allowed(IPAddress.Parse(ip)));

    [Fact]
    public void LabelFallsBackToTextThenFileName()
    {
        Assert.Equal("问候", PhoneInput.Label(new JsonObject { ["name"] = "问候", ["text"] = "你好" }));
        Assert.Equal("你好", PhoneInput.Label(new JsonObject { ["name"] = " ", ["text"] = "你好" }));
        Assert.Equal("掌声", PhoneInput.Label(new JsonObject { ["name"] = "", ["text"] = "", ["audio"] = @"D:\音效\掌声.mp3" }));
    }

    [Fact]
    public void ConfigGetsTokenAndValidPort()
    {
        var cfg = ConfigStore.Normalize(new JsonObject { ["phone_port"] = 80 }, "zh");
        Assert.Equal(8721, cfg.Num("phone_port"));
        var token = cfg.Str("phone_token");
        Assert.Equal(16, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        // 已有的令牌保留，手机收藏的链接还能用
        Assert.Equal(token, ConfigStore.Normalize(cfg, "zh").Str("phone_token"));
        Assert.NotEqual(token, ConfigStore.Normalize(null, "zh").Str("phone_token"));
    }
}
