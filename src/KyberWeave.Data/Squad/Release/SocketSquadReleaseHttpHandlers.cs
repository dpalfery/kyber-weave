using System.Diagnostics.CodeAnalysis;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Squad.Release;

/// <summary>Creates the sockets transport a Squad release download runs on, with automatic redirects off.</summary>
public sealed class SocketSquadReleaseHttpHandlers : ISquadReleaseHttpHandlers
{
    /// <inheritdoc />
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler's lifetime transfers to the caller, which disposes it with the source that uses it.")]
    public HttpMessageHandler Create() => new SocketsHttpHandler { AllowAutoRedirect = false };
}
