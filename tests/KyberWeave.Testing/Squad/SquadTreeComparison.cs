namespace KyberWeave.Testing.Squad;

/// <summary>Compares a whole-tree snapshot, or one entry of it, and says what differs in words a failure can print.</summary>
public static class SquadTreeComparison
{
    /// <summary>What differs between two trees.</summary>
    /// <param name="expected">The tree that should be there.</param>
    /// <param name="actual">The tree that is there.</param>
    /// <returns>Null when they match; otherwise the first difference, in ordinal path order.</returns>
    public static string? Difference(
        IReadOnlyDictionary<string, SquadTreeEntry> expected,
        IReadOnlyDictionary<string, SquadTreeEntry> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        foreach (string path in expected.Keys.Where(path => !actual.ContainsKey(path)).Order(StringComparer.Ordinal))
        {
            return $"'{path}' is missing";
        }

        foreach (string path in actual.Keys.Where(path => !expected.ContainsKey(path)).Order(StringComparer.Ordinal))
        {
            return $"'{path}' is unexpected";
        }

        foreach ((string path, SquadTreeEntry expectedEntry) in expected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string? difference = EntryDifference(path, expectedEntry, actual[path]);
            if (difference is not null)
            {
                return difference;
            }
        }

        return null;
    }

    /// <summary>What differs between one expected entry and the entry actually there.</summary>
    /// <param name="path">The path, for the message.</param>
    /// <param name="expected">The entry that should be there.</param>
    /// <param name="actual">The entry that is there.</param>
    /// <returns>Null when they match; otherwise what differs.</returns>
    public static string? EntryDifference(string path, SquadTreeEntry expected, SquadTreeEntry actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (expected.Kind != actual.Kind)
        {
            return $"'{path}' is a {actual.Kind} where a {expected.Kind} was expected";
        }

        if (!string.Equals(expected.LinkTarget, actual.LinkTarget, StringComparison.Ordinal))
        {
            return $"'{path}' names '{actual.LinkTarget}' where it named '{expected.LinkTarget}'";
        }

        ReadOnlyMemory<byte>? expectedBytes = expected.Content;
        ReadOnlyMemory<byte>? actualBytes = actual.Content;
        if (expectedBytes.HasValue != actualBytes.HasValue)
        {
            return $"'{path}' has different content";
        }

        if (expectedBytes is { } left && actualBytes is { } right && !left.Span.SequenceEqual(right.Span))
        {
            return $"'{path}' changed from {Digest(left)} to {Digest(right)}";
        }

        return null;
    }

    private static string Digest(ReadOnlyMemory<byte> content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content.Span))[..12];
}

/// <summary>Checks the files a flow must never touch against the baseline, without throwing on a misconfigured path.</summary>
public static class SquadCrashMarkerCheck
{
    /// <summary>Every way the untouched files differ from the baseline.</summary>
    /// <param name="untouchedPaths">The paths the flow must not change.</param>
    /// <param name="baseline">The tree the flow started from.</param>
    /// <param name="actualTree">The tree the run left.</param>
    /// <returns>One read failure per problem; empty when every untouched file is intact.</returns>
    public static IReadOnlyList<string> Problems(
        IReadOnlyList<string> untouchedPaths,
        IReadOnlyDictionary<string, SquadTreeEntry> baseline,
        IReadOnlyDictionary<string, SquadTreeEntry> actualTree)
    {
        ArgumentNullException.ThrowIfNull(untouchedPaths);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(actualTree);

        List<string> problems = [];
        foreach (string marker in untouchedPaths)
        {
            if (!baseline.TryGetValue(marker, out SquadTreeEntry? expected))
            {
                problems.Add($"the suite names '{marker}' as untouched but the baseline does not contain it");
                continue;
            }

            if (!actualTree.TryGetValue(marker, out SquadTreeEntry? actual))
            {
                problems.Add($"a file the flow must not touch is missing: '{marker}'");
                continue;
            }

            string? difference = SquadTreeComparison.EntryDifference(marker, expected, actual);
            if (difference is not null)
            {
                problems.Add($"a file the flow must not touch changed: {difference}");
            }
        }

        return problems;
    }
}
