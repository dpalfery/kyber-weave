using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Deterministic <see cref="ISquadReleaseSource"/> fake that copies the checked-in
/// <c>products/kyber-squad</c> corpus into the extraction destination on every
/// <see cref="DownloadAndExtractAsync"/> call.
/// </summary>
/// <remarks>
/// <see cref="FakeSquadReleaseSource"/> writes only <c>squad.yml</c> and <c>toolchain.yml</c>,
/// which is enough for lock and receipt bookkeeping but not for
/// <see cref="KyberWeave.Core.Squad.Parsing.SquadSourceLoader.Load(string)"/>: there is no
/// bundle, no agents, and no skills on disk for it to load. A lifecycle test that must assert a
/// real R17-derived file count, a real per-target path prefix, or coexistence between two real
/// targets needs the genuine corpus tree, not the two-file manifest fixture, so this fake copies
/// the repository's own <c>products/kyber-squad</c> directory byte-for-byte instead of
/// fabricating one.
/// Pre-caches file contents in memory to avoid repeated filesystem walks and cold file reads.
/// </remarks>
public sealed class CorpusSquadReleaseSource : ISquadReleaseSource
{
    private const string AssetSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly string CorpusRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly Lazy<List<(string RelativePath, byte[] Content)>> PreloadedCorpus = new(LoadCorpus);

    /// <summary>
    /// Copies the checked-in Squad corpus into the request's destination, standing in for a
    /// downloaded and verified release archive.
    /// </summary>
    public Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> extractedFiles = CopyCorpus(request.DestinationPath);

        string assetName = $"kyber-squad-{request.Version}.zip";
        SquadReleaseResult result = new(
            request.Version,
            new SquadReleaseAsset(
                assetName,
                new Uri($"https://github.com/{request.Repository}/releases/download/v{request.Version}/{assetName}"),
                1024),
            new SquadReleaseChecksum(
                assetName,
                AssetSha256),
            Path.GetFullPath(request.DestinationPath),
            extractedFiles);

        return Task.FromResult(result);
    }

    public void Dispose()
    {
        // No unmanaged resources.
    }

    private static List<(string RelativePath, byte[] Content)> LoadCorpus()
    {
        List<(string RelativePath, byte[] Content)> files = [];
        foreach (string sourceFilePath in Directory.EnumerateFiles(CorpusRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(CorpusRoot, sourceFilePath).Replace(Path.DirectorySeparatorChar, '/');
            files.Add((relativePath, File.ReadAllBytes(sourceFilePath)));
        }

        files.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    private static List<string> CopyCorpus(string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        List<(string RelativePath, byte[] Content)> preloaded = PreloadedCorpus.Value;
        List<string> extractedFiles = new(preloaded.Count);

        foreach ((string relativePath, byte[] content) in preloaded)
        {
            string destinationFilePath = Path.Combine(destinationRoot, relativePath);
            string? destinationFileDirectory = Path.GetDirectoryName(destinationFilePath);
            if (destinationFileDirectory is not null)
            {
                Directory.CreateDirectory(destinationFileDirectory);
            }

            File.WriteAllBytes(destinationFilePath, content);
            extractedFiles.Add(relativePath);
        }

        return extractedFiles;
    }
}
