using System.Text.Json;

namespace KyberWeave.Core.Review;

/// <summary>
/// The lenses a review cycle decided not to seat, with each reason, kept in the cycle folder as
/// <c>not-seated.json</c>. <c>review seats</c> writes the file-pattern and decision-model decisions, the gate run adds
/// the gate-proven ones, and <c>review verdict</c> reads them, so a reviewer never retypes a reason the CLI decided.
/// </summary>
public static class NotSeatedRecord
{
    /// <summary>The record's file name inside a cycle folder.</summary>
    public const string FileName = "not-seated.json";

    /// <summary>Adds <paramref name="decisions"/> to the cycle's record, keeping the decisions already in it.</summary>
    /// <exception cref="IOException">The record could not be written.</exception>
    public static void Add(string cycleFolder, IReadOnlyDictionary<string, string> decisions) =>
        Replace(cycleFolder, [], decisions);

    /// <summary>
    /// Records <paramref name="decisions"/> as the current answer for the <paramref name="owned"/> lenses: an earlier
    /// entry for an owned lens that is no longer decided is removed, so a re-run never leaves a reason that stopped
    /// holding (a gate that passed before and fails now).
    /// </summary>
    /// <exception cref="IOException">The record could not be written.</exception>
    public static void Replace(string cycleFolder, IEnumerable<string> owned, IReadOnlyDictionary<string, string> decisions)
    {
        ArgumentNullException.ThrowIfNull(cycleFolder);
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(decisions);
        Dictionary<string, string> merged = new(Read(cycleFolder), StringComparer.Ordinal);
        foreach (string lens in owned)
            merged.Remove(lens);
        foreach ((string lens, string reason) in decisions)
            merged[lens] = reason;

        Directory.CreateDirectory(cycleFolder);
        File.WriteAllText(
            Path.Combine(cycleFolder, FileName),
            JsonSerializer.Serialize(new SortedDictionary<string, string>(merged, StringComparer.Ordinal), Indented));
    }

    /// <summary>The cycle's recorded decisions; empty when there is no record or it cannot be read.</summary>
    public static IReadOnlyDictionary<string, string> Read(string cycleFolder)
    {
        ArgumentNullException.ThrowIfNull(cycleFolder);
        string path = Path.Combine(cycleFolder, FileName);
        try
        {
            return File.Exists(path)
                   && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } read
                ? read
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The newest <c>cycle-&lt;n&gt;</c> folder under <paramref name="reviewFolder"/> that holds a <c>seats</c> folder,
    /// or null when there is none.
    /// </summary>
    public static string? LatestCycle(string reviewFolder)
    {
        ArgumentNullException.ThrowIfNull(reviewFolder);
        if (!Directory.Exists(reviewFolder))
            return null;

        return Directory.EnumerateDirectories(reviewFolder, "cycle-*")
            .Select(folder => (folder, number: int.TryParse(Path.GetFileName(folder)["cycle-".Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 0))
            .Where(entry => entry.number > 0 && Directory.Exists(Path.Combine(entry.folder, "seats")))
            .OrderByDescending(entry => entry.number)
            .Select(entry => entry.folder)
            .FirstOrDefault();
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
