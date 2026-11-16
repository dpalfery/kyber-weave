using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Keeps the not-seated record of a review cycle in its folder.</summary>
public sealed class FileNotSeatedStore : INotSeatedStore
{
    /// <inheritdoc />
    public void Add(string cycleFolder, IReadOnlyDictionary<string, string> decisions) =>
        Replace(cycleFolder, [], decisions);

    /// <inheritdoc />
    public void Replace(string cycleFolder, IEnumerable<string> owned, IReadOnlyDictionary<string, string> decisions)
    {
        ArgumentNullException.ThrowIfNull(cycleFolder);
        string text = NotSeatedRecord.Merge(Read(cycleFolder), owned, decisions);

        Directory.CreateDirectory(cycleFolder);
        File.WriteAllText(Path.Combine(cycleFolder, NotSeatedRecord.FileName), text);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Read(string cycleFolder)
    {
        ArgumentNullException.ThrowIfNull(cycleFolder);
        string path = Path.Combine(cycleFolder, NotSeatedRecord.FileName);
        try
        {
            return NotSeatedRecord.Parse(File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public string? LatestCycle(string reviewFolder)
    {
        ArgumentNullException.ThrowIfNull(reviewFolder);
        if (!Directory.Exists(reviewFolder))
            return null;

        return Directory.EnumerateDirectories(reviewFolder, "cycle-*")
            .Select(folder => (folder, number: NotSeatedRecord.CycleNumber(Path.GetFileName(folder))))
            .Where(entry => entry.number > 0 && Directory.Exists(Path.Combine(entry.folder, "seats")))
            .OrderByDescending(entry => entry.number)
            .Select(entry => entry.folder)
            .FirstOrDefault();
    }
}
