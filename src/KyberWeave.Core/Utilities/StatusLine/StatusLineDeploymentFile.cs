namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>One status-line artifact to deploy, as a portable relative path and its bytes.</summary>
/// <remarks>
/// Mirrors <c>KyberWeave.Core.Squad.Deployment.SquadDeploymentFile</c>. The path is portable —
/// forward slashes, NFC, no segment that a Windows filesystem would alias — and relative to the
/// harness staging root; every plan resolves it through <c>SquadPathPolicy</c>, so an artifact can
/// never name a location outside the Kyber-owned root it is deployed into.
/// </remarks>
public sealed record StatusLineDeploymentFile
{
    public StatusLineDeploymentFile(string relativePath, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(content);
        RelativePath = relativePath;
        Content = content;
    }

    /// <summary>The portable, forward-slash relative path beneath the harness staging root.</summary>
    public string RelativePath { get; init; }

    /// <summary>The exact bytes Kyber Utilities will own at that path.</summary>
    public ReadOnlyMemory<byte> Content { get; }
}
