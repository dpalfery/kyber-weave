using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Core.Squad.Release;

/// <summary>Downloads and extracts a release only after its transport and identity are verified.</summary>
public interface ISquadReleaseSource : IDisposable
{
    /// <summary>Downloads, verifies, and safely extracts one Squad release.</summary>
    Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken);
}
