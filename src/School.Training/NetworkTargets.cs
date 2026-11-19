using System.Net;
using System.Net.Sockets;

namespace School.Training;

/// <summary>
/// Outbound connection policy for operator-configured equipment targets.
/// Probes and terminal sessions run inside the service process, so an administrator
/// account must not be able to turn them into a port scanner for the host itself,
/// the link-local segment (including cloud metadata endpoints) or multicast groups.
/// Host names are resolved first and every resolved address must pass, so a DNS name
/// cannot be used to reach a blocked address.
/// </summary>
public static class NetworkTargets
{
    public const string BlockedMessage = "不允许连接本机回环、链路本地、组播或未指定地址；请填写设备或跳板机的实际局域网地址。";
    public const string UnresolvableMessage = "无法解析目标主机名，请检查地址或 DNS 配置。";

    /// <summary>
    /// Returns the addresses that may be connected to, or a user-facing reason.
    /// Callers connect to a returned address rather than re-resolving the name, so a
    /// second DNS answer cannot redirect the connection after the check.
    /// </summary>
    public static async Task<(bool Allowed, string Message, IPAddress[] Addresses)> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host)) return (false, UnresolvableMessage, []);
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host.Trim(), out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken);
        }
        catch (SocketException) { return (false, UnresolvableMessage, []); }
        catch (ArgumentException) { return (false, UnresolvableMessage, []); }
        if (addresses.Length == 0) return (false, UnresolvableMessage, []);
        // Try IPv4 first: training equipment is reached over IPv4, and a dual-stack name whose
        // AAAA record is unreachable would otherwise burn the whole probe timeout.
        var allowed = addresses.Where(address => !IsBlocked(address))
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0)
            .ToArray();
        return allowed.Length == 0 ? (false, BlockedMessage, []) : (true, "", allowed);
    }

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0) return true;                       // 0.0.0.0/8 本网络/未指定
            if (bytes[0] >= 224) return true;                     // 组播、保留与广播段
            if (bytes[0] == 169 && bytes[1] == 254) return true;  // 链路本地（含云元数据地址）
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None);
        return true;
    }
}
