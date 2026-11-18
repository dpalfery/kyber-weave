using System.Net;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The network seam of the local embedding endpoint: the HTTP transport it is called over, and name resolution.</summary>
/// <remarks>
/// The embedding client pins every connection to loopback. A real transport repeats that check when it connects, and a
/// fake answers from a script, so the client's request and response handling is tested without a socket.
/// </remarks>
public interface IEmbeddingTransport
{
    /// <summary>Creates a transport that refuses to connect anywhere but loopback and never follows a redirect.</summary>
    /// <returns>A new handler. The caller owns it and disposes it with the client that uses it.</returns>
    HttpMessageHandler CreateHandler();

    /// <summary>Resolves a host name to the addresses a connection to it would use.</summary>
    /// <param name="host">The host name or address literal.</param>
    /// <returns>The addresses.</returns>
    /// <exception cref="System.Net.Sockets.SocketException">The host could not be resolved.</exception>
    IReadOnlyList<IPAddress> ResolveHost(string host);
}
