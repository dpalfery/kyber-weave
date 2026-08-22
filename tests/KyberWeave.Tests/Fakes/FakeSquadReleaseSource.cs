using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Deterministic in-memory fake implementing <see cref="ISquadReleaseSource"/> for lifecycle testing.
/// </summary>
public sealed class FakeSquadReleaseSource : ISquadReleaseSource
{
    private readonly List<SquadReleaseRequest> _requests = [];
    private readonly string _assetSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public IReadOnlyList<SquadReleaseRequest> Requests => _requests;

    public async Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Add(request);

        Directory.CreateDirectory(request.DestinationPath);

        // Write the standard minimal squad.yml and toolchain.yml into the destination.
        List<string> extractedFiles = [];

        string squadYmlPath = Path.Combine(request.DestinationPath, "squad.yml");
        await File.WriteAllTextAsync(squadYmlPath, "schema: kyber-squad.squad/v1\nversion-source: kyber-weave-assembly\n", cancellationToken);
        extractedFiles.Add("squad.yml");

        // Mirrors products/kyber-squad/toolchain.yml byte for byte in the shape that
        // matters: no validated-release. A fake that pinned a fabricated digest here kept
        // the suite green while every real install failed lock validation, so this default
        // must keep tracking the canonical file. A test needing a pinned upstream build
        // writes its own toolchain.yml into the destination after this fake returns.
        string toolchainYmlPath = Path.Combine(request.DestinationPath, "toolchain.yml");
        await File.WriteAllTextAsync(
            toolchainYmlPath,
            """
            schema: kyber-squad.toolchain/v1
            required-features: []
            validated-release: null
            """,
            cancellationToken);
        extractedFiles.Add("toolchain.yml");

        string assetName = $"kyber-squad-{request.Version}.zip";
        SquadReleaseResult result = new(
            Version: request.Version,
            Asset: new SquadReleaseAsset(
                Name: assetName,
                DownloadUri: new Uri($"https://github.com/{request.Repository}/releases/download/v{request.Version}/{assetName}"),
                Size: 1024),
            Checksum: new SquadReleaseChecksum(
                AssetName: assetName,
                Sha256: _assetSha256),
            ExtractionRoot: Path.GetFullPath(request.DestinationPath),
            ExtractedFiles: extractedFiles);

        return result;
    }

    public void Dispose()
    {
        // No unmanaged resources
    }
}
