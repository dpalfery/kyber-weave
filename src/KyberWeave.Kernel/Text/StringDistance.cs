namespace KyberWeave.Core.Text;

/// <summary>
/// Provides string distance algorithms such as Levenshtein distance.
/// </summary>
public static class StringDistance
{
    /// <summary>
    /// Computes the Levenshtein distance between two strings.
    /// </summary>
    /// <param name="a">The source string.</param>
    /// <param name="b">The target string.</param>
    /// <param name="ignoreCase">Whether comparison should ignore character casing.</param>
    /// <returns>The minimum number of single-character edits (insertions, deletions, substitutions) required to transform <paramref name="a"/> into <paramref name="b"/>.</returns>
    /// <remarks>
    /// Implements the Wagner-Fischer dynamic programming algorithm using two row buffers to achieve
    /// O(min(m, n)) memory overhead and O(m * n) time complexity. When <paramref name="ignoreCase"/>
    /// is true, characters are compared using <see cref="char.ToLowerInvariant(char)"/>.
    /// </remarks>
    public static int Levenshtein(string a, string b, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (string.Equals(a, b, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return 0;
        }

        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        // Keep the second string as the shorter one to minimize buffer memory allocation.
        if (a.Length < b.Length)
        {
            (a, b) = (b, a);
        }

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            char charA = a[i - 1];

            for (int j = 1; j <= b.Length; j++)
            {
                char charB = b[j - 1];
                bool match = ignoreCase
                    ? char.ToLowerInvariant(charA) == char.ToLowerInvariant(charB)
                    : charA == charB;
                int cost = match ? 0 : 1;

                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>Closest candidate by edit distance, when one is plausibly a typo.</summary>
    /// <param name="value">The unrecognised input.</param>
    /// <param name="candidates">The known values; none may be null.</param>
    /// <returns>
    /// The candidate with the smallest case-insensitive <see cref="Levenshtein"/> distance (the first
    /// on a tie), or <see langword="null"/> when there are no candidates or the closest is more than
    /// <c>max(3, value.Length / 2)</c> edits away.
    /// </returns>
    public static string? Nearest(string value, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(candidates);

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in candidates)
        {
            int distance = Levenshtein(value, candidate, true);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        if (best is null) return null;
        int threshold = Math.Max(3, value.Length / 2);
        return bestDistance <= threshold ? best : null;
    }
}
