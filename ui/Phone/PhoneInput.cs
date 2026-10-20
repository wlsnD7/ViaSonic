using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace ViaSonic.Phone;

/// <summary>
/// 手机输入：在手机浏览器里打字，用电脑朗读。按配置开关网页服务，处理防火墙、端口、链接。
/// OnConfig、Allow、NewLink 在界面线程调用，Changed 也在界面线程触发。
/// </summary>
public sealed class PhoneInput : IPhoneHost, IDisposable
{
    private readonly App _app;
    private PhoneServer? _server;
    private int _port, _gen;
    private bool _wasOn, _started;
    private volatile string _token = "";

    /// <summary>本机的局域网地址，打开手机输入时和网络变化时刷新。</summary>
    public List<LanAddress> Addresses { get; private set; } = [];
    public bool PortInUse { get; private set; }
    /// <summary>防火墙是否放行；null：还在检查。</summary>
    public bool? FirewallOk { get; private set; }
    /// <summary>正在等用户在 UAC 里确认。</summary>
    public bool Allowing { get; private set; }

    public event Action? Changed;

    public PhoneInput(App app)
    {
        _app = app;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    public string Token => _token;

    /// <summary>选中的网络；不在了就用第一个。</summary>
    public LanAddress? Address => Addresses.FirstOrDefault(a => a.Ip == _app.Str("phone_ip")) ?? Addresses.FirstOrDefault();

    /// <summary>手机扫的链接；没有网络时为空。</summary>
    public string Url => Address is { } a ? $"http://{a.Ip}:{_port}/?k={_token}" : "";

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');

    /// <summary>启动时和 phone_* 配置变了时调用。启动时不弹 UAC，用户打开开关时才弹。</summary>
    public void OnConfig()
    {
        bool on = _app.Bool("phone_enabled");
        _token = _app.Str("phone_token");
        bool turnedOn = on && !_wasOn;
        bool ask = turnedOn && _started;
        _wasOn = on;
        _started = true;
        if (!on)
        {
            _gen++;
            StopServer();
            FirewallOk = null;
            PortInUse = false;
            Changed?.Invoke();
            return;
        }
        if (turnedOn)
        {
            Addresses = Lan.List();
            _ = StartAsync(ask);
        }
        else if (FirewallOk != null && (int)_app.Num("phone_port") != _port) Listen(); // 改了端口
        Changed?.Invoke();
    }

    /// <summary>先处理防火墙再监听：已经有放行规则时，Windows 不会再弹它自己的防火墙提示。</summary>
    private async Task StartAsync(bool ask)
    {
        int gen = ++_gen;
        FirewallOk = null;
        _port = (int)_app.Num("phone_port");
        var ok = await Task.Run(Firewall.Allowed);
        if (gen != _gen) return;
        if (!ok && ask)
        {
            Allowing = true;
            Changed?.Invoke();
            ok = await Task.Run(() => Firewall.Allow() && Firewall.Allowed());
            Allowing = false;
            if (gen != _gen) return;
        }
        FirewallOk = ok;
        Listen();
        Changed?.Invoke();
    }

    /// <summary>卡片上的「允许」。</summary>
    public async void Allow()
    {
        if (Allowing) return;
        Allowing = true;
        Changed?.Invoke();
        var ok = await Task.Run(() => Firewall.Allow() && Firewall.Allowed());
        Allowing = false;
        if (_app.Bool("phone_enabled")) FirewallOk = ok;
        Changed?.Invoke();
    }

    /// <summary>「更换链接」：旧链接马上失效。</summary>
    public void NewLink() => _app.Set("phone_token", NewToken());

    private void Listen()
    {
        StopServer();
        _port = (int)_app.Num("phone_port");
        try
        {
            _server = new PhoneServer(this, _port);
            PortInUse = false;
        }
        catch (SocketException) // 被占用，或者在系统保留的端口范围里（Hyper-V 等）
        {
            PortInUse = true;
        }
    }

    private void StopServer()
    {
        _server?.Dispose();
        _server = null;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _app.Post(() =>
    {
        if (!_app.Bool("phone_enabled")) return;
        Addresses = Lan.List();
        Changed?.Invoke();
    });

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _gen++;
        StopServer();
    }

    // ------------------------------------------------------------------ 手机页面的操作（网页服务的线程上调用）
    public Task<bool> SayAsync(string text)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _app.Speaker.Speak(text, done: ok => tcs.TrySetResult(ok));
        return tcs.Task;
    }

    public void Stop() => _app.Post(_app.Speaker.Stop);

    public void Repeat() => _app.Post(_app.Speaker.Repeat);

    public Task<bool> PlayAsync(string id) => OnUi(() =>
    {
        var p = _app.Phrases.FirstOrDefault(x => x.Str("id") == id);
        if (p == null || !Playable(p)) return false;
        _app.PlayPhrase(p);
        return true;
    }, false);

    public Task<IReadOnlyList<PhoneItem>> PhrasesAsync() => OnUi<IReadOnlyList<PhoneItem>>(() =>
        [.. _app.Phrases.OfType<JsonNode>().Where(Playable).Select(p => new PhoneItem(p.Str("id"), Label(p), p.Bool("loop")))], []);

    /// <summary>常用语页新加的空白条目不显示。</summary>
    private static bool Playable(JsonNode p) => p.Str("text").Trim().Length > 0 || p.Str("audio").Length > 0;

    /// <summary>名称；没有名称时用文字，音频条目用文件名。</summary>
    internal static string Label(JsonNode p)
    {
        var name = p.Str("name").Trim();
        if (name.Length > 0) return name;
        var text = p.Str("text").Trim();
        return text.Length > 0 ? text : Path.GetFileNameWithoutExtension(p.Str("audio"));
    }

    /// <summary>配置只在界面线程读写：投递过去执行，拿回结果。</summary>
    private Task<TResult> OnUi<TResult>(Func<TResult> f, TResult fallback)
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_app.Post(() =>
            {
                try { tcs.TrySetResult(f()); }
                finally { tcs.TrySetResult(fallback); }
            }))
            tcs.TrySetResult(fallback);
        return tcs.Task;
    }
}
