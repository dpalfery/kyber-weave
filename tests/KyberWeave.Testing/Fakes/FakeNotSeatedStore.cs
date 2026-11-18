using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="INotSeatedStore"/>: keeps the decisions each cycle records in memory.</summary>
/// <remarks>
/// A fake records what a caller decided; the file format is the adapter's business and is proven by the store's own
/// composition tests. An empty instance records nothing and reads back nothing, which is the safe answer for a caller
/// test that does not exercise seating.
/// </remarks>
internal sealed class FakeNotSeatedStore : INotSeatedStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _decisions = new(StringComparer.Ordinal);

    /// <summary>Each replace, in order: the folder, the owned lenses and the decisions given.</summary>
    public List<(string Folder, IReadOnlyList<string> Owned, IReadOnlyDictionary<string, string> Decisions)> Replaced { get; } = [];

    /// <summary>The newest cycle each review folder reports, by review folder; null when not named.</summary>
    public Dictionary<string, string?> LatestCycles { get; init; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Add(string cycleFolder, IReadOnlyDictionary<string, string> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (!_decisions.TryGetValue(cycleFolder, out Dictionary<string, string>? kept))
        {
            kept = new Dictionary<string, string>(StringComparer.Ordinal);
            _decisions[cycleFolder] = kept;
        }

        foreach ((string lens, string reason) in decisions)
            kept[lens] = reason;
    }

    /// <inheritdoc />
    public void Replace(string cycleFolder, IEnumerable<string> owned, IReadOnlyDictionary<string, string> decisions)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(decisions);
        string[] lenses = [.. owned];
        Replaced.Add((cycleFolder, lenses, decisions));

        // The file-backed store merges into what is already recorded: an owned lens is replaced or removed, and a lens
        // the caller does not own keeps its earlier decision, so a caller test sees the record the product would read.
        Dictionary<string, string> merged = _decisions.TryGetValue(cycleFolder, out Dictionary<string, string>? kept)
            ? new Dictionary<string, string>(kept, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string lens in lenses)
            merged.Remove(lens);
        foreach ((string lens, string reason) in decisions)
            merged[lens] = reason;

        _decisions[cycleFolder] = merged;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Read(string cycleFolder) =>
        _decisions.TryGetValue(cycleFolder, out Dictionary<string, string>? kept)
            ? kept
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? LatestCycle(string reviewFolder) =>
        LatestCycles.TryGetValue(reviewFolder, out string? latest) ? latest : null;
}
