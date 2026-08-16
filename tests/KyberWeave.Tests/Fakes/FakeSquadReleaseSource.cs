using System.Text;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Deterministic in-memory fake implementing <see cref="ISquadReleaseSource"/> for lifecycle testing.
/// </summary>
public sealed class FakeSquadReleaseSource : ISquadReleaseSource
{
    private readonly List<SquadReleaseRequest> _requests = [];
    private Func<SquadReleaseRequest, SquadReleaseResult>? _handler;
    private string? _failure;
    private string _assetSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private readonly Dictionary<string, byte[]> _customFiles = new(StringComparer.Ordinal);

    public IReadOnlyList<SquadReleaseRequest> Requests => _requests;

    public FakeSquadReleaseSource WithHandler(Func<SquadReleaseRequest, SquadReleaseResult> handler)
    {
        _handler = handler;
        return this;
    }

    public FakeSquadReleaseSource WithFailure(string errorMessage)
    {
        _failure = errorMessage;
        return this;
    }

    public FakeSquadReleaseSource WithAssetSha256(string sha256)
    {
        _assetSha256 = sha256;
        return this;
    }

    public FakeSquadReleaseSource WithFile(string relativePath, string content)
    {
        _customFiles[relativePath] = Encoding.UTF8.GetBytes(content);
        return this;
    }

    public FakeSquadReleaseSource WithFile(string relativePath, byte[] content)
    {
        _customFiles[relativePath] = content;
        return this;
    }

    public async Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests.Add(request);

        if (_failure is not null)
        {
            throw new InvalidOperationException(_failure);
        }

        if (_handler is not null)
        {
            return _handler(request);
        }

        Directory.CreateDirectory(request.DestinationPath);

        // Write default or custom release source files into the extraction destination
        List<string> extractedFiles = [];

        if (_customFiles.Count > 0)
        {
            foreach ((string relPath, byte[] bytes) in _customFiles)
            {
                string fullPath = Path.Combine(request.DestinationPath, relPath);
                string? parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);
                extractedFiles.Add(relPath);
            }
        }
        else
        {
            // Write standard minimal squad.yml and toolchain.yml
            string squadYmlPath = Path.Combine(request.DestinationPath, "squad.yml");
            await File.WriteAllTextAsync(squadYmlPath, "schema: kyber-squad.squad/v1\nversion-source: kyber-weave-assembly\n", cancellationToken);
            extractedFiles.Add("squad.yml");

            // Mirrors products/kyber-squad/toolchain.yml byte for byte in the shape that
            // matters: no validated-release. A fake that pinned a fabricated digest here
            // kept the suite green while every real install failed lock validation, so this
            // default must keep tracking the canonical file. Tests needing a pinned upstream
            // build supply their own toolchain.yml through the file map above.
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
        }

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
