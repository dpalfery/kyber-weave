using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>Stages a local kyber-squad checkout as the release source in place of a download.</summary>
/// <remarks>
/// The checkout carries the same companion skills as a release archive, from the same <see cref="SquadCompanionSkills"/>
/// type, so a CLI run from a checkout deploys what an installed release deploys.
/// </remarks>
internal sealed class LocalCheckoutSquadReleaseSource : ISquadReleaseSource
{
    private readonly string _sourceRoot;

    public LocalCheckoutSquadReleaseSource(string sourceRoot)
    {
        _sourceRoot = sourceRoot;
    }

    /// <remarks>
    /// The companion skills are resolved before the destination is created. A name clash throws from here, so a
    /// failed extraction leaves nothing behind and reports nothing as extracted.
    /// </remarks>
    public Task<SquadReleaseResult> DownloadAndExtractAsync(SquadReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(string RelativePath, string SourcePath)> companionFiles =
            SquadCompanionSkills.Collect(_sourceRoot, SquadCompanionSkills.ResolveRoot(_sourceRoot));

        Directory.CreateDirectory(request.DestinationPath);
        List<string> extractedFiles = [];

        foreach (string sourceFilePath in Directory.EnumerateFiles(_sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(_sourceRoot, sourceFilePath);
            string destinationFilePath = Path.Combine(request.DestinationPath, relativePath);
            CopyFile(sourceFilePath, destinationFilePath);
            extractedFiles.Add(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
        }

        foreach ((string relativePath, string sourceFilePath) in companionFiles)
        {
            string destinationFilePath = Path.Combine(
                request.DestinationPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            CopyFile(sourceFilePath, destinationFilePath);
            extractedFiles.Add(relativePath);
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

    private static void CopyFile(string sourceFilePath, string destinationFilePath)
    {
        string? destinationFileDirectory = Path.GetDirectoryName(destinationFilePath);
        if (destinationFileDirectory is not null) Directory.CreateDirectory(destinationFileDirectory);

        File.Copy(sourceFilePath, destinationFilePath, true);
    }

    public void Dispose()
    {
    }
}
