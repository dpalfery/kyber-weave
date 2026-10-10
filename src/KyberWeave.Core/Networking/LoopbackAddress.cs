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
        IPAddress normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return IPAddress.IsLoopback(normalized);
    }

    /// <summary>
    /// Whether a URI host names the loopback interface. The one host check every caller
    /// shares, so "is this endpoint local?" cannot drift between the validator that
    /// allows plain HTTP and the key resolver that withholds the key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The address is parsed rather than pattern-matched. A first-octet test reads
    /// <c>127.0.0.1.evil.com</c>, <c>127.0.evil</c> and <c>127.1.2.3.4.5</c> as loopback,
    /// which sends plain HTTP to a remote host and suppresses the key there;
    /// <see cref="IPAddress.TryParse(string, out IPAddress)"/> rejects all three, so only a
    /// real address reaches <see cref="IsLoopback(IPAddress)"/>. That covers the whole
    /// 127/8 range, <c>::1</c> in every written form, and IPv4-mapped IPv6. Brackets are
    /// trimmed because <see cref="Uri.Host"/> keeps them on an IPv6 literal.
    /// </para>
    /// <para>
    /// The one name that is not an address is <c>localhost</c>, and one trailing dot is
    /// insignificant in it: <c>localhost.</c> is the fully qualified spelling of the same
    /// name and resolves to the loopback interface. Neither the exact spelling test nor
    /// <see cref="IPAddress.TryParse(string, out IPAddress)"/> accepts the dotted form, so
    /// without this the two callers could read the same endpoint differently -- which is
    /// the drift the shared helper exists to prevent. Exactly one dot is dropped, because
    /// <c>localhost.</c> is this name and <c>localhost..</c> is not.
    /// </para>
    /// </remarks>
    public static bool IsLoopbackHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        string normalized = host.Trim().Trim('[', ']');
        if (string.Equals(TrimRootDot(normalized), "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(normalized, out IPAddress? address) && IsLoopback(address);
    }

    /// <summary>The name with a single trailing root dot removed, and otherwise unchanged.</summary>
    /// <remarks>
    /// Only one dot goes. Trimming them all would make <c>localhost..</c> and
    /// <c>localhost.evil.com.</c> name this host too, which is exactly the lookalike the
    /// parsed-address path exists to refuse.
    /// </remarks>
    private static string TrimRootDot(string host) =>
        host.Length > 1 && host.EndsWith('.') ? host[..^1] : host;
}
