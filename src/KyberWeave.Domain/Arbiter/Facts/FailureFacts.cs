namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>One worker failure read back from the failure store: the decision that judged it, and its text.</summary>
/// <param name="DecisionId">The decision record that judged the failure when it was stored.</param>
/// <param name="Text">The failure's text, exactly as the rework dispatch carried it.</param>
internal sealed record FailureRecord(string DecisionId, string Text);

/// <summary>
/// Reads the rework dispatch's failure cluster and its current failure text, and shapes the stored failure line
/// the same-failure rule compares against. A stored failure carries its cluster key and the decision that judged it
/// ahead of its text, so a later rework of the same cluster finds its prior.
/// </summary>
internal static class FailureFacts
{
    /// <summary>The trigger a rework dispatch carrying the failure-cluster marker derives.</summary>
    public const string Trigger = "delegate.failure";

    /// <summary>The step-0 and step-1 fact holding the earlier failure of the cluster, absent for a first failure.</summary>
    public const string PreviousText = "failure.previous-text";

    /// <summary>The step-1 fact holding the failure this rework dispatch carries.</summary>
    public const string CurrentText = "failure.current-text";

    private const string ClusterPrefix = "cluster: ";
    private const string DecisionPrefix = "decision: ";
    private const string UnknownDecision = "unknown";
    private const string FailureSection = "FAILURE:";

    /// <summary>The cluster key the dispatch names on its first body line, or null when it names none.</summary>
    public static string? ClusterOf(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        string prefix = TriggerClassifier.FailureClusterMarker + ":";
        string? first = BodyLines(prompt).FirstOrDefault();
        if (first is null || !first.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string key = first[prefix.Length..].Trim();
        return key.Length > 0 ? key : null;
    }

    /// <summary>
    /// The failing output the dispatch carries: the lines after its <c>FAILURE:</c> line, up to the first blank line.
    /// Null when the section is absent or empty.
    /// </summary>
    public static string? CurrentTextOf(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        List<string> lines = RawLines(prompt);
        int start = lines.FindIndex(line => string.Equals(line, FailureSection, StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        string text = string.Join("\n", lines.Skip(start + 1).TakeWhile(line => line.Length > 0)).Trim();
        return text.Length > 0 ? text : null;
    }

    /// <summary>The line a failure is stored under: its cluster, the decision that judged it, then its text.</summary>
    public static string Stored(string cluster, string decisionId, string text) =>
        ClusterPrefix + cluster + "\n" + DecisionPrefix + decisionId + "\n" + text;

    /// <summary>The latest stored failure of <paramref name="cluster"/>, or null when the cluster has none.</summary>
    public static FailureRecord? LastFor(IReadOnlyList<string> stored, string cluster)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(cluster);
        FailureRecord? last = null;
        foreach (string item in stored)
        {
            string[] lines = item.Split('\n', 3);
            if (lines.Length < 3
                || !lines[0].StartsWith(ClusterPrefix, StringComparison.Ordinal)
                || !string.Equals(lines[0][ClusterPrefix.Length..], cluster, StringComparison.Ordinal))
            {
                continue;
            }

            string decision = lines[1].StartsWith(DecisionPrefix, StringComparison.Ordinal)
                ? lines[1][DecisionPrefix.Length..]
                : UnknownDecision;
            last = new FailureRecord(decision, lines[2]);
        }

        return last;
    }

    /// <summary>The body with its header block stripped, every line trimmed, blank lines kept.</summary>
    private static List<string> RawLines(string prompt) =>
        HeaderBlock.Strip(prompt)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();

    /// <summary>The non-blank body lines, trimmed, in order.</summary>
    private static IEnumerable<string> BodyLines(string prompt) =>
        RawLines(prompt).Where(line => line.Length > 0);
}
