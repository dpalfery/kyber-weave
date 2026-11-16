namespace KyberWeave.Core.Squad.Release;

/// <summary>The immutable coordinates of a Squad release extraction.</summary>
/// <param name="Repository">The GitHub repository in <c>owner/name</c> form.</param>
/// <param name="Version">The release version without a leading <c>v</c>.</param>
/// <param name="DestinationPath">The new directory that receives the verified archive.</param>
public sealed record SquadReleaseRequest(
    string Repository,
    string Version,
    string DestinationPath);

/// <summary>An asset declared by the matching GitHub release.</summary>
/// <param name="Name">The exact published asset name.</param>
/// <param name="DownloadUri">The asset's HTTPS download URI.</param>
/// <param name="Size">The size declared by GitHub, in bytes.</param>
public sealed record SquadReleaseAsset(string Name, Uri DownloadUri, long Size);

/// <summary>The exact SHA-256 identity selected from the release checksum manifest.</summary>
/// <param name="AssetName">The exact asset name covered by the digest.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 digest.</param>
public sealed record SquadReleaseChecksum(string AssetName, string Sha256);

/// <summary>The identity and files of a verified, safely extracted Squad release.</summary>
/// <param name="Version">The release version without a leading <c>v</c>.</param>
/// <param name="Asset">The downloaded release asset.</param>
/// <param name="Checksum">The verified checksum row.</param>
/// <param name="ExtractionRoot">The absolute destination directory.</param>
/// <param name="ExtractedFiles">Archive-relative file paths in ordinal order.</param>
public sealed record SquadReleaseResult(
    string Version,
    SquadReleaseAsset Asset,
    SquadReleaseChecksum Checksum,
    string ExtractionRoot,
    IReadOnlyList<string> ExtractedFiles);

/// <summary>Downloads and extracts a release only after its transport and identity are verified.</summary>
public interface ISquadReleaseSource : IDisposable
{
    /// <summary>Downloads, verifies, and safely extracts one Squad release.</summary>
    Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken);
}
