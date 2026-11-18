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

        if (!files.DirectoryExists(reviewFolder))
            return [];

        List<ReviewCycleRecord> cycles = [];
        foreach (string folder in files.EnumerateDirectories(reviewFolder))
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

        string full = paths.GetFullPath(priorFolder, environment.CurrentDirectory);
        if (ReviewCyclePlanning.TryNumber(Path.GetFileName(full)) is not { } number)
            return files.DirectoryExists(full) ? [Read(0, full)] : [];

        string parent = Path.GetDirectoryName(full) ?? full;
        return [.. ReadFinished(parent).Where(cycle => cycle.Number < number), Read(number, full)];
    }

    /// <inheritdoc />
    public void WriteIdentity(string cycleFolder, ReviewCycleIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleFolder);
        ArgumentNullException.ThrowIfNull(identity);

        files.CreateDirectory(cycleFolder);
        files.WriteAllText(
            Path.Combine(cycleFolder, ReviewCyclePlanning.IdentityFile),
            ReviewCyclePlanning.SerializeIdentity(identity));
    }

    /// <inheritdoc />
    public ReviewCycleIdentity? ReadRecordedIdentity(string cycleFolder) =>
        ReviewCyclePlanning.ReadRecordedIdentity(ReadText(Path.Combine(cycleFolder, ReviewCyclePlanning.IdentityFile)));

    /// <inheritdoc />
    public bool HasIdentity(string cycleFolder) => files.FileExists(Path.Combine(cycleFolder, ReviewCyclePlanning.IdentityFile));

    /// <inheritdoc />
    public IReadOnlyList<string> TaskFolders(string scratchpad) =>
        files.DirectoryExists(scratchpad)
            ? [.. files.EnumerateDirectories(scratchpad)
                .Where(dir => files.DirectoryExists(Path.Combine(dir, "review")))
                .Select(dir => Path.GetFileName(dir))
                .Order(StringComparer.Ordinal)]
            : [];

    private ReviewCycleRecord Read(int number, string folder) =>
        ReviewCyclePlanning.Record(
            number,
            folder,
            ReadText(Path.Combine(folder, ReviewCyclePlanning.HeadFile), failOnUnreadable: true),
            ReadText(Path.Combine(folder, ReviewCyclePlanning.IdentityFile)),
            ReadText(Path.Combine(folder, ReviewCyclePlanning.GatesFile)));

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
