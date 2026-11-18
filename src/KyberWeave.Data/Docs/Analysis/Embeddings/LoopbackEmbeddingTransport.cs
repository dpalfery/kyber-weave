using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Networking;

namespace KyberWeave.Data.Docs.Analysis.Embeddings;

/// <summary>The real transport for the local embedding endpoint: sockets that connect only to loopback, and system name resolution.</summary>
/// <remarks>
/// The connect callback resolves the host again and refuses any address outside loopback, so a name that changes its
/// answer between the client's check and the connection cannot move the call off the machine.
/// </remarks>
public sealed class LoopbackEmbeddingTransport : IEmbeddingTransport
{
    /// <inheritdoc />
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler's lifetime transfers to the caller, which disposes it with the client that uses it.")]
    public HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = ConnectLoopback
    };

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> ResolveHost(string host) => Dns.GetHostAddresses(host);

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "A successful NetworkStream takes socket ownership; every failed socket is disposed in the catch block.")]
    private static async ValueTask<Stream> ConnectLoopback(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !LoopbackAddress.IsLoopback(address)))
            throw new HttpRequestException("The embedding endpoint must resolve only to loopback addresses.");

        Exception? lastFailure = null;
        foreach (IPAddress address in addresses)
        {
            Socket socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
        }

        throw new HttpRequestException("No loopback address accepted the embedding connection.", lastFailure);
    }
}
