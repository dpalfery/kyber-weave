using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Reads and writes review cycle folders on the file system.</summary>
public sealed class FileReviewCycleStore : IReviewCycleStore
{
    /// <inheritdoc />
    public IReadOnlyList<ReviewCycleRecord> ReadFinished(string reviewFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewFolder);

        if (!Directory.Exists(reviewFolder))
            return [];

        List<ReviewCycleRecord> cycles = [];
        foreach (string folder in Directory.EnumerateDirectories(reviewFolder))
        {
            if (ReviewCyclePlanning.TryNumber(Path.GetFileName(folder)) is not { } number
                || !File.Exists(Path.Combine(folder, ReviewCyclePlanning.FindingsFile)))
                continue;

            cycles.Add(Read(number, folder));
        }

        return [.. cycles.OrderBy(cycle => cycle.Number)];
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewCycleRecord> ReadThrough(string priorFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(priorFolder);

        string full = Path.GetFullPath(priorFolder);
        if (ReviewCyclePlanning.TryNumber(Path.GetFileName(full)) is not { } number)
            return Directory.Exists(full) ? [Read(0, full)] : [];

        string parent = Path.GetDirectoryName(full) ?? full;
        return [.. ReadFinished(parent).Where(cycle => cycle.Number < number), Read(number, full)];
    }

    /// <inheritdoc />
    public void WriteIdentity(string cycleFolder, ReviewCycleIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleFolder);
        ArgumentNullException.ThrowIfNull(identity);

        Directory.CreateDirectory(cycleFolder);
        File.WriteAllText(
            Path.Combine(cycleFolder, ReviewCyclePlanning.IdentityFile),
            ReviewCyclePlanning.SerializeIdentity(identity));
    }

    /// <inheritdoc />
    public ReviewCycleIdentity? ReadRecordedIdentity(string cycleFolder) =>
        ReviewCyclePlanning.ReadRecordedIdentity(ReadText(Path.Combine(cycleFolder, ReviewCyclePlanning.IdentityFile)));

    /// <inheritdoc />
    public bool HasIdentity(string cycleFolder) => File.Exists(Path.Combine(cycleFolder, ReviewCyclePlanning.IdentityFile));

    /// <inheritdoc />
    public IReadOnlyList<string> TaskFolders(string scratchpad) =>
        Directory.Exists(scratchpad)
            ? [.. Directory.EnumerateDirectories(scratchpad)
                .Where(dir => Directory.Exists(Path.Combine(dir, "review")))
                .Select(dir => Path.GetFileName(dir))
                .Order(StringComparer.Ordinal)]
            : [];

    private static ReviewCycleRecord Read(int number, string folder) =>
        ReviewCyclePlanning.Record(
            number,
            folder,
            ReadText(Path.Combine(folder, ReviewCyclePlanning.HeadFile), failOnUnreadable: true),
            ReadText(Path.Combine(folder, ReviewCyclePlanning.IdentityFile)),
            ReadText(Path.Combine(folder, ReviewCyclePlanning.GatesFile)));

    private static string? ReadText(string path, bool failOnUnreadable = false)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (!failOnUnreadable && FileFailure.Is(ex))
        {
            return null;
        }
    }
}
