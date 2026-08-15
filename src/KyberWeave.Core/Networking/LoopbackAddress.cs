using System.Net;

namespace KyberWeave.Core.Networking;

/// <summary>Canonical loopback checks shared by configuration and connection-time policy.</summary>
internal static class LoopbackAddress
{
    /// <summary>
    /// Treats IPv4-mapped IPv6 addresses according to their mapped IPv4 value. This is
    /// required for the complete 127/8 range because <see cref="IPAddress.IsLoopback"/>
    /// recognizes mapped 127.0.0.1 but not every mapped 127/8 address consistently.
    /// </summary>
    public static bool IsLoopback(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(normalized);
    }
}
