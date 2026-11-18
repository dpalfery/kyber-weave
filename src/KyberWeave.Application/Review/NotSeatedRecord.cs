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

    /// <summary>The prefix of a cycle folder's name.</summary>
    public const string CycleFolderPrefix = "cycle-";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The decisions a record's text holds; empty when the text is absent or is not a record.</summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string? text) =>
        text is not null && JsonSerializer.Deserialize<Dictionary<string, string>>(text) is { } read
            ? read
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The record's text once <paramref name="decisions"/> are the current answer for the <paramref name="owned"/> lenses:
    /// an earlier entry for an owned lens that is no longer decided is removed.
    /// </summary>
    public static string Merge(
        IReadOnlyDictionary<string, string> existing, IEnumerable<string> owned, IReadOnlyDictionary<string, string> decisions)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(decisions);
        Dictionary<string, string> merged = new(existing, StringComparer.Ordinal);
        foreach (string lens in owned)
            merged.Remove(lens);
        foreach ((string lens, string reason) in decisions)
            merged[lens] = reason;

        return JsonSerializer.Serialize(new SortedDictionary<string, string>(merged, StringComparer.Ordinal), Indented);
    }

    /// <summary>The cycle number in a <c>cycle-&lt;n&gt;</c> folder name, or zero when the name is not one.</summary>
    public static int CycleNumber(string folderName) =>
        folderName.StartsWith(CycleFolderPrefix, StringComparison.Ordinal)
        && int.TryParse(folderName.AsSpan(CycleFolderPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
}
