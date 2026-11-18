using System.Globalization;

namespace KyberWeave.Core.Review;

/// <summary>The rule that turns measured declarations into <see cref="DuplicateCluster"/>s.</summary>
public static class DuplicateClusters
{
    /// <summary>
    /// Groups declarations that already passed the threshold by shared body, strongest cluster first.
    /// </summary>
    /// <remarks>
    /// Read-only: the caller measured each body, so this decides only which bodies repeat. A caller
    /// measuring bodies keeps the comparable kinds filtered, so a mixed list cannot widen the
    /// comparison here.
    /// </remarks>
    /// <param name="measured">Each declaration's body hash, its line count and the declaration itself.</param>
    /// <returns>The clusters whose body repeats, largest body first, then by id.</returns>
    public static IReadOnlyList<DuplicateCluster> Merge(
        IEnumerable<(string Key, int Lines, DuplicateMember Member)> measured)
    {
        ArgumentNullException.ThrowIfNull(measured);

        Dictionary<string, (int Lines, List<DuplicateMember> Members)> byBody = new(StringComparer.Ordinal);
        foreach ((string key, int lines, DuplicateMember member) in measured)
        {
            if (!byBody.TryGetValue(key, out (int Lines, List<DuplicateMember> Members) entry))
            {
                entry = (lines, []);
                byBody[key] = entry;
            }

            entry.Members.Add(member);
        }

        return
        [
            .. byBody
                .Where(pair => pair.Value.Members.Count > 1)
                .Select(pair => new DuplicateCluster(
                    ClusterId(pair.Key),
                    pair.Value.Lines,
                    [.. pair.Value.Members
                        .OrderBy(m => m.File, StringComparer.Ordinal)
                        .ThenBy(m => m.StartLine)]))
                .OrderByDescending(c => c.NormalizedLines)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
        ];
    }

    private static string ClusterId(string hash) =>
        string.Create(CultureInfo.InvariantCulture, $"dup-{hash[..8]}");
}
