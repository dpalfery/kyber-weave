using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>Stages a local kyber-squad checkout as the release source in place of a download.</summary>
internal sealed class LocalCheckoutSquadReleaseSource : ISquadReleaseSource
{
    private readonly string _sourceRoot;

    public LocalCheckoutSquadReleaseSource(string sourceRoot)
    {
        _sourceRoot = sourceRoot;
    }

    public Task<SquadReleaseResult> DownloadAndExtractAsync(SquadReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(request.DestinationPath);
        List<string> extractedFiles = [];

        foreach (string sourceFilePath in Directory.EnumerateFiles(_sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(_sourceRoot, sourceFilePath);
            string destinationFilePath = Path.Combine(request.DestinationPath, relativePath);
            string? destinationFileDirectory = Path.GetDirectoryName(destinationFilePath);
            if (destinationFileDirectory is not null) Directory.CreateDirectory(destinationFileDirectory);

            File.Copy(sourceFilePath, destinationFilePath, true);
            extractedFiles.Add(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
        }

        extractedFiles.Sort(StringComparer.Ordinal);
        string assetName = $"kyber-squad-{request.Version}.zip";
        SquadReleaseResult result = new(
            request.Version,
            new SquadReleaseAsset(assetName, new Uri($"https://localhost/{assetName}"), 1024),
            new SquadReleaseChecksum(assetName, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
            Path.GetFullPath(request.DestinationPath),
            extractedFiles);

        return Task.FromResult(result);
    }

    public void Dispose()
    {
    }
}
