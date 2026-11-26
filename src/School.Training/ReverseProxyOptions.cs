using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using IPNetwork = System.Net.IPNetwork;

namespace School.Training;

/// <summary>
/// Production reverse proxies that may supply forwarded client, scheme and host.
/// An empty list leaves the headers ignored, so a browser cannot spoof its address
/// to slip past the login limits or the local-only setup check.
/// </summary>
public sealed class ReverseProxyOptions
{
    public const string ConnectionPeerItem = "School.ConnectionPeer";

    public IReadOnlyList<IPAddress> TrustedProxies { get; }
    public IReadOnlyList<IPNetwork> TrustedNetworks { get; }
    public IReadOnlyList<string> AllowedHosts { get; }
    public bool IsConfigured => TrustedProxies.Count > 0 || TrustedNetworks.Count > 0;

    ReverseProxyOptions(IReadOnlyList<IPAddress> proxies, IReadOnlyList<IPNetwork> networks, IReadOnlyList<string> allowedHosts)
    {
        TrustedProxies = proxies;
        TrustedNetworks = networks;
        AllowedHosts = allowedHosts;
    }

    public static ReverseProxyOptions Load(IConfiguration configuration)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        foreach (var raw in configuration.GetSection("School:ReverseProxy:TrustedProxies").Get<string[]>() ?? [])
        {
            var entry = (raw ?? "").Trim();
            if (entry.Length == 0) continue;
            if (entry.StartsWith('[') && entry.EndsWith(']') && entry.Length >= 2)
                entry = entry[1..^1];
            if (entry.Contains('/'))
            {
                if (!IPNetwork.TryParse(entry, out var network))
                    throw new InvalidOperationException($"School:ReverseProxy:TrustedProxies 无法解析网段：{Preview(entry)}");
                if (network.PrefixLength == 0)
                    throw new InvalidOperationException("School:ReverseProxy:TrustedProxies 不能信任全部地址，请填写学校反向代理的具体地址或网段");
                if (!networks.Any(existing => existing.Equals(network))) networks.Add(network);
                continue;
            }
            if (!IPAddress.TryParse(entry, out var address))
                throw new InvalidOperationException($"School:ReverseProxy:TrustedProxies 无法解析地址：{Preview(entry)}");
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (!proxies.Any(existing => existing.Equals(address))) proxies.Add(address);
        }

        var hosts = configuration.GetSection("School:ReverseProxy:AllowedHosts").Get<string[]>()
            ?? (configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ReverseProxyOptions(proxies, networks, hosts
            .Select(host => host.Trim())
            .Where(host => host.Length > 0 && host != "*")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public void Apply(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in TrustedProxies) options.KnownProxies.Add(proxy);
        foreach (var network in TrustedNetworks) options.KnownIPNetworks.Add(network);
        foreach (var host in AllowedHosts) options.AllowedHosts.Add(host);
    }

    /// <summary>
    /// Socket peer captured before forwarded headers rewrite <see cref="HttpContext.Connection"/>.
    /// Setup authorization must use this, not the forwarded client address.
    /// </summary>
    public static IPAddress? ConnectionPeer(HttpContext ctx)
        => ctx.Items.TryGetValue(ConnectionPeerItem, out var saved) && saved is IPAddress peer
            ? peer
            : ctx.Connection.RemoteIpAddress;

    static string Preview(string entry) => entry.Length <= 64 ? entry : entry[..64];
}
