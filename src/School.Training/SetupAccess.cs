using System.Net;

namespace School.Training;

public static class SetupAccess
{
    // Pass the socket peer from before forwarded-header rewriting. A trusted proxy may
    // replace RemoteIpAddress with the client, and that client must not become "local".
    // A server browser using its trusted HTTPS hostname may connect over its own LAN address.
    public static bool IsLocalEndpoint(IPAddress? remote, IPAddress? local)
    {
        if (remote == null || local == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        if (local.Equals(IPAddress.Any) || local.Equals(IPAddress.IPv6Any)) return false;
        if (IPAddress.IsLoopback(remote)) return true;
        return remote.Equals(local);
    }
}
