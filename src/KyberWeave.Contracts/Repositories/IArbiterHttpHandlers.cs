namespace KyberWeave.Contracts.Repositories;

/// <summary>Creates the HTTP transport the Arbiter's provider calls and its Ollama probe run on.</summary>
public interface IArbiterHttpHandlers
{
    /// <summary>Creates a transport. The caller owns it and disposes it with the client that uses it.</summary>
    /// <returns>A new handler.</returns>
    HttpMessageHandler Create();
}
