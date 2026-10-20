using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ViaSonic.Phone;

/// <summary>手机页面上的一条常用语。</summary>
public sealed record PhoneItem(string Id, string Label, bool Loop);

/// <summary>手机页面能做的事，由 PhoneInput 实现（测试里用假的）。可能在任意线程调用。</summary>
public interface IPhoneHost
{
    /// <summary>链接里的令牌，不对就拒绝。更换链接后立即生效。</summary>
    string Token { get; }
    /// <summary>读完（或被停止、被打断）时完成；合成失败时为 false。</summary>
    Task<bool> SayAsync(string text);
    void Stop();
    void Repeat();
    /// <summary>电脑上已经删掉了这条常用语时返回 false，手机重新读取列表。</summary>
    Task<bool> PlayAsync(string id);
    Task<IReadOnlyList<PhoneItem>> PhrasesAsync();
}

/// <summary>
/// 手机输入的局域网网页服务：GET / 返回手机页面，/api/* 朗读、停止、重复、常用语。
/// 朗读分两步：POST /api/say 马上返回编号，GET /api/wait?id= 等这句读完才回复（手机这时才清空输入框）。
/// 用 TcpListener 自己解析 HTTP（HttpListener 监听局域网地址要管理员权限注册 URL）。
/// 每个请求处理完就断开连接，只接受局域网里的设备。
/// </summary>
public sealed class PhoneServer : IDisposable
{
    public const int MaxText = 500;
    /// <summary>朗读请求最多等这么久就先回复，手机照样清空输入框。</summary>
    internal static TimeSpan SayWait = TimeSpan.FromMinutes(3);
    private const int MaxHeader = 8192, MaxBody = 4096, MaxConnections = 16;

    private readonly IPhoneHost _host;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections);
    private readonly Dictionary<long, Task<bool>> _saying = [];
    private long _sayId;
    private readonly object _rateLock = new();
    private double _tokens = Burst;
    private long _lastRefill = Environment.TickCount64;
    private const double Burst = 10, PerSecond = 5;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>端口被占用时抛 SocketException（SocketError.AddressAlreadyInUse）。</summary>
    public PhoneServer(IPhoneHost host, int port, IPAddress? bind = null)
    {
        _host = host;
        _listener = new TcpListener(bind ?? IPAddress.Any, port) { ExclusiveAddressUse = true };
        _listener.Start();
        _ = AcceptLoop();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) when (_cts.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint ep || !Allowed(ep.Address)) return;
            if (!await _slots.WaitAsync(0)) return;
            try
            {
                var stream = client.GetStream();
                Request? req;
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    read.CancelAfter(10_000);
                    req = await Request.ReadAsync(stream, read.Token);
                }
                // 朗读请求要等读完才回复，不受上面的超时限制
                var res = req == null ? Response.Status(400) : await Handle(req);
                using var write = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                write.CancelAfter(10_000);
                await res.WriteAsync(stream, write.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
            catch (Exception e) { Log.Error(T("未处理的异常"), e); }
            finally { _slots.Release(); }
        }
    }

    internal async Task<Response> Handle(Request r)
    {
        var k = r.Query.GetValueOrDefault("k") ?? "";
        bool ok = TokenOk(k);
        switch (r.Method, r.Path)
        {
            case ("GET", "/"): return Response.Html(PhonePage.Render(ok ? k : null));
            case ("GET", "/manifest.webmanifest"):
                return ok ? new Response(200, "application/manifest+json", PhonePage.Manifest(k)) : Response.Status(403);
            case ("GET", "/icon.png"): return PhonePage.Icon is { } icon ? new Response(200, "image/png", icon) : Response.Status(404);
        }
        if (!r.Path.StartsWith("/api/")) return Response.Status(404);
        if (!ok) return Response.Status(403);
        if (r.Method == "GET" && r.Path == "/api/phrases") return Response.Json(PhrasesJson(await _host.PhrasesAsync()));
        if (r.Method == "GET" && r.Path == "/api/wait") return await Wait(r.Query.GetValueOrDefault("id"));
        if (r.Method != "POST") return Response.Status(405);
        if (!Take()) return Response.Status(429);
        switch (r.Path)
        {
            case "/api/say":
                var text = Clean(Encoding.UTF8.GetString(r.Body));
                if (text.Length == 0) return Response.Status(400);
                if (text.Length > MaxText) return Response.Status(413);
                return Response.Json(Encoding.ASCII.GetBytes($"{{\"id\":{Track(_host.SayAsync(text))}}}"));
            case "/api/stop":
                _host.Stop();
                return Response.Status(204);
            case "/api/repeat":
                _host.Repeat();
                return Response.Status(204);
            case "/api/play":
                return await _host.PlayAsync(r.Query.GetValueOrDefault("id") ?? "") ? Response.Status(204) : Response.Status(404);
        }
        return Response.Status(404);
    }

    /// <summary>记下这句朗读，读完一分钟后忘掉（等待请求可能晚一点才到）。</summary>
    private long Track(Task<bool> said)
    {
        long id;
        lock (_saying)
        {
            id = ++_sayId;
            _saying[id] = said;
        }
        _ = said.ContinueWith(async _ =>
        {
            await Task.Delay(60_000);
            lock (_saying) _saying.Remove(id);
        }, TaskScheduler.Default);
        return id;
    }

    /// <summary>204：读完了、被停止或被打断（或者等太久了）；500：合成失败，手机保留文字。</summary>
    private async Task<Response> Wait(string? idText)
    {
        Task<bool>? said = null;
        if (long.TryParse(idText, out var id))
            lock (_saying) _saying.TryGetValue(id, out said);
        if (said == null) return Response.Status(204);
        if (await Task.WhenAny(said, Task.Delay(SayWait, _cts.Token)) != said) return Response.Status(204);
        return await said ? Response.Status(204) : Response.Status(500);
    }

    private bool TokenOk(string k)
    {
        var token = _host.Token;
        if (token.Length == 0 || k.Length != token.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(k), Encoding.ASCII.GetBytes(token));
    }

    /// <summary>朗读、播放等操作最多连续 10 次，之后每秒 5 次。</summary>
    private bool Take()
    {
        lock (_rateLock)
        {
            var now = Environment.TickCount64;
            _tokens = Math.Min(Burst, _tokens + (now - _lastRefill) / 1000.0 * PerSecond);
            _lastRefill = now;
            if (_tokens < 1) return false;
            _tokens--;
            return true;
        }
    }

    /// <summary>换行变成空格，去掉其他控制字符。</summary>
    internal static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '\r' or '\n' or '\t') sb.Append(' ');
            else if (!char.IsControl(ch)) sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static byte[] PhrasesJson(IReadOnlyList<PhoneItem> items)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var p in items)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("label", p.Label);
                w.WriteBoolean("loop", p.Loop);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ 只接受局域网
    /// <summary>本机、私有地址、链路本地地址，或者和本机某个网卡在同一网段。</summary>
    internal static bool Allowed(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal;
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IsPrivate(a)) return true;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && SameSubnet(a, u.Address, u.IPv4Mask)) return true;
        }
        catch (NetworkInformationException) { }
        return false;
    }

    internal static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    private static bool SameSubnet(IPAddress a, IPAddress local, IPAddress mask)
    {
        var x = a.GetAddressBytes();
        var y = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (m.All(v => v == 0)) return false;
        for (int i = 0; i < 4; i++)
            if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    // ------------------------------------------------------------------ HTTP
    internal sealed record Request(string Method, string Path, Dictionary<string, string> Query, byte[] Body)
    {
        /// <summary>读一个请求。格式不对、太大时返回 null。</summary>
        public static async Task<Request?> ReadAsync(Stream s, CancellationToken ct)
        {
            var buf = new byte[MaxHeader + 4 + MaxBody];
            int len = 0, end;
            while ((end = HeaderEnd(buf, len)) < 0)
            {
                if (len >= MaxHeader) return null;
                int n = await s.ReadAsync(buf.AsMemory(len, MaxHeader - len), ct);
                if (n == 0) return null;
                len += n;
            }
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.")) return null;
            int contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && (!int.TryParse(line[(colon + 1)..].Trim(), out contentLength) || contentLength is < 0 or > MaxBody)) return null;
            }
            int bodyStart = end + 4;
            while (len < bodyStart + contentLength)
            {
                int n = await s.ReadAsync(buf.AsMemory(len, bodyStart + contentLength - len), ct);
                if (n == 0) return null;
                len += n;
            }
            var target = first[1];
            int q = target.IndexOf('?');
            var path = q < 0 ? target : target[..q];
            return new Request(first[0], path, ParseQuery(q < 0 ? "" : target[(q + 1)..]), buf[bodyStart..(bodyStart + contentLength)]);
        }

        private static int HeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
            return -1;
        }

        internal static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                var key = eq < 0 ? part : part[..eq];
                var value = eq < 0 ? "" : part[(eq + 1)..];
                try { d[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' ')); }
                catch (UriFormatException) { }
            }
            return d;
        }
    }

    internal sealed record Response(int Code, string Type, byte[] Body)
    {
        public static Response Status(int code) => new(code, "text/plain; charset=utf-8", []);
        public static Response Html(string html) => new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
        public static Response Json(byte[] json) => new(200, "application/json; charset=utf-8", json);

        public async Task WriteAsync(Stream s, CancellationToken ct)
        {
            var head = new StringBuilder()
                .Append("HTTP/1.1 ").Append(Code).Append(' ').Append(Reason(Code)).Append("\r\n")
                .Append("Content-Type: ").Append(Type).Append("\r\n")
                .Append("Content-Length: ").Append(Body.Length).Append("\r\n")
                .Append("Cache-Control: no-store\r\n")
                // 令牌在地址里：不随 Referer 发出去
                .Append("Referrer-Policy: no-referrer\r\n")
                .Append("X-Content-Type-Options: nosniff\r\n")
                .Append("Content-Security-Policy: default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:\r\n")
                .Append("Connection: close\r\n\r\n");
            await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
            if (Body.Length > 0) await s.WriteAsync(Body, ct);
            await s.FlushAsync(ct);
        }

        private static string Reason(int code) => code switch
        {
            200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 413 => "Payload Too Large", 429 => "Too Many Requests", 500 => "Internal Server Error", _ => "Error",
        };
    }
}
