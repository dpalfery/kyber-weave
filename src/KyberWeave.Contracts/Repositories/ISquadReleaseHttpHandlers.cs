namespace KyberWeave.Contracts.Repositories;

/// <summary>Creates the HTTP transport a Squad release download runs on.</summary>
public interface ISquadReleaseHttpHandlers
{
    /// <summary>Creates a transport that does not follow redirects, so the download can inspect every one before it follows it.</summary>
    /// <returns>A new handler. The caller owns it and disposes it with the source that uses it.</returns>
    HttpMessageHandler Create();
}
