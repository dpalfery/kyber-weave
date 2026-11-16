using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Arbiter;

/// <summary>Creates the default HTTP transport the Arbiter's provider and Ollama probe run on.</summary>
public sealed class SocketArbiterHttpHandlers : IArbiterHttpHandlers
{
    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler's lifetime transfers to the caller, which disposes it with the client that uses it.")]
    public HttpMessageHandler Create() => new HttpClientHandler();
}
