using KyberWeave.Core;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Reads and writes review cycle folders through the file-system seam.</summary>
/// <param name="files">Writes and reads every cycle folder entry.</param>
/// <param name="paths">Resolves a folder the caller spelled relatively.</param>
/// <param name="environment">Names the working directory a relative folder resolves against.</param>
public sealed class FileReviewCycleStore(
    IWritableFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IReviewCycleStore
{
    /// <inheritdoc />
    public IReadOnlyList<ReviewCycleRecord> ReadFinished(string reviewFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewFolder);

        string resolved = Resolve(reviewFolder);
        if (!files.DirectoryExists(resolved))
            return [];

        List<ReviewCycleRecord> cycles = [];
        foreach (string folder in files.EnumerateDirectories(resolved))
        {
            if (ReviewCyclePlanning.TryNumber(Path.GetFileName(folder)) is not { } number
                || !files.FileExists(Path.Combine(folder, ReviewCyclePlanning.FindingsFile)))
                continue;

            cycles.Add(Read(number, folder));
        }

        return [.. cycles.OrderBy(cycle => cycle.Number)];
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewCycleRecord> ReadThrough(string priorFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(priorFolder);

        string full = Resolve(priorFolder);
        if (ReviewCyclePlanning.TryNumber(Path.GetFileName(full)) is not { } number)
            return files.DirectoryExists(full) ? [Read(0, full)] : [];

        string parent = Path.GetDirectoryName(full) ?? full;
        return [.. ReadFinished(parent).Where(cycle => cycle.Number < number), Read(number, full)];
    }

    /// <inheritdoc />
    public ReviewCycleRecord? ReadCycle(string cycleFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleFolder);

        string resolved = Resolve(cycleFolder);
        return ReviewCyclePlanning.TryNumber(Path.GetFileName(resolved)) is { } number
            ? Read(number, resolved)
            : null;
    }

    /// <inheritdoc />
    public void WriteIdentity(string cycleFolder, ReviewCycleIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleFolder);
        ArgumentNullException.ThrowIfNull(identity);

        string resolved = Resolve(cycleFolder);
        files.CreateDirectory(resolved);
        files.WriteAllText(
            Path.Combine(resolved, ReviewCyclePlanning.IdentityFile),
            ReviewCyclePlanning.SerializeIdentity(identity));
    }

    /// <inheritdoc />
    public ReviewCycleIdentity? ReadRecordedIdentity(string cycleFolder) =>
        ReviewCyclePlanning.ReadRecordedIdentity(
            ReadText(Path.Combine(Resolve(cycleFolder), ReviewCyclePlanning.IdentityFile)));

    /// <inheritdoc />
    public bool HasIdentity(string cycleFolder) =>
        files.FileExists(Path.Combine(Resolve(cycleFolder), ReviewCyclePlanning.IdentityFile));

    /// <inheritdoc />
    public IReadOnlyList<string> TaskFolders(string scratchpad) =>
        files.DirectoryExists(Resolve(scratchpad))
            ? [.. files.EnumerateDirectories(Resolve(scratchpad))
                .Where(dir => files.DirectoryExists(Path.Combine(dir, ScratchLayout.ReviewFolderName)))
                .Select(dir => Path.GetFileName(dir))
                .Order(StringComparer.Ordinal)]
            : [];

    /// <summary>
    /// The folder as the file system names it: a caller may spell it relative to the working directory, and the seam
    /// resolves it the same way for every member. An empty folder names nothing to resolve and passes through.
    /// </summary>
    private string Resolve(string folder) =>
        string.IsNullOrWhiteSpace(folder) ? folder : paths.GetFullPath(folder, environment.CurrentDirectory);

    private ReviewCycleRecord Read(int number, string folder) =>
        ReviewCyclePlanning.Record(
            number,
            folder,
            ReadText(Path.Combine(folder, ReviewCyclePlanning.HeadFile), failOnUnreadable: true),
            ReadText(Path.Combine(folder, ReviewCyclePlanning.IdentityFile)),
            ReadText(Path.Combine(folder, ScratchLayout.GatesFileName)));

    private string? ReadText(string path, bool failOnUnreadable = false)
    {
        if (!files.FileExists(path))
            return null;

        try
        {
            return files.ReadAllText(path);
        }
        catch (Exception ex) when (!failOnUnreadable && FileFailure.Is(ex))
        {
            return null;
        }
    }
}
