using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ViaSonic.Phone;

/// <summary>本机在局域网里的 IPv4 地址，用来生成手机扫的链接。</summary>
public sealed record LanAddress(string Ip, string Name);

public static class Lan
{
    // 虚拟机、WSL、VPN、代理的 TUN 网卡：手机连不到这些地址
    private static readonly string[] VirtualWords =
        ["Hyper-V", "vEthernet", "VMware", "VirtualBox", "WSL", "TAP-", "Wintun", "WireGuard", "Tailscale", "ZeroTier",
         "Clash", "Mihomo", "sing-box", "Npcap", "Bluetooth", "Loopback"];

    /// <summary>有默认网关的网卡排在前面，其中私有地址优先。</summary>
    public static List<LanAddress> List()
    {
        var found = new List<(LanAddress Addr, int Rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || VirtualWords.Any(w => ni.Description.Contains(w, StringComparison.OrdinalIgnoreCase) || ni.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var props = ni.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var u in props.UnicastAddresses)
                {
                    var a = u.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork || !Usable(a)) continue;
                    var addr = new LanAddress(a.ToString(), ni.Name);
                    if (found.All(f => f.Addr.Ip != addr.Ip))
                        found.Add((addr, (gateway ? 0 : 2) + (PhoneServer.IsPrivate(a) ? 0 : 1)));
                }
            }
        }
        catch (NetworkInformationException) { }
        return [.. found.OrderBy(f => f.Rank).Select(f => f.Addr)];
    }

    /// <summary>去掉本机、链路本地（169.254，没拿到地址）、198.18/15（代理软件的虚拟地址）。</summary>
    private static bool Usable(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] != 127 && !(b[0] == 169 && b[1] == 254) && !(b[0] == 198 && b[1] is 18 or 19) && b[0] < 224 && b[0] != 0;
    }
}
