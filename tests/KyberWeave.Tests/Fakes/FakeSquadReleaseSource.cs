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

            string toolchainYmlPath = Path.Combine(request.DestinationPath, "toolchain.yml");
            await File.WriteAllTextAsync(
                toolchainYmlPath,
                """
                schema: kyber-squad.toolchain/v1
                required-features:
                  - agent-ir/v1
                  - semantic-permissions/v1
                  - structured-degradation/v1
                  - agent-to-skill-lowering/v1
                validated-release:
                  version: 0.28.0
                  tag-commit: e041462f4a48086dbee3da145c07d71b8a3b84fd
                  asset-sha256: e041462f4a48086dbee3da145c07d71b8a3b84fde041462f4a48086dbee3da14
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
