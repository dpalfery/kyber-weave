using KyberWeave.Core.Review;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     What <c>review cycle</c>, <c>review seats</c> and the council's <c>review gates</c> share: describing a cycle
///     plan in the same lines, and reading the cycle a gate report's path belongs to.
/// </summary>
public static class ReviewCycleView
{
    private const string FolderPrefix = "cycle-";

    /// <summary>
    ///     The review folder and cycle number a gate report written to <paramref name="outPath"/> belongs to, when the path
    ///     is <c>.../review/cycle-&lt;n&gt;/&lt;file&gt;</c>.
    /// </summary>
    public static bool TryFromOutPath(string outPath, out string reviewFolder, out int cycle)
    {
        reviewFolder = string.Empty;
        cycle = 0;
        string? cycleFolder = Path.GetDirectoryName(Path.GetFullPath(outPath));
        string? name = cycleFolder is null ? null : Path.GetFileName(cycleFolder);
        string? parent = cycleFolder is null ? null : Path.GetDirectoryName(cycleFolder);
        if (name is null || parent is null
            || !name.StartsWith(FolderPrefix, StringComparison.Ordinal)
            || !int.TryParse(name.AsSpan(FolderPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out cycle)
            || cycle <= 0
            || !string.Equals(Path.GetFileName(parent), "review", StringComparison.Ordinal))
        {
            return false;
        }

        reviewFolder = parent;
        return true;
    }

    /// <summary>The lines that describe a verifier cycle: the prior cycle, the findings to re-check and what each gate does.</summary>
    public static IEnumerable<string> VerifierLines(ReviewCyclePlan plan, IReadOnlyList<ReviewFinding> recheck)
    {
        if (plan.Prior is not { } prior)
            yield break;

        yield return $"prior: {prior.Folder}";
        yield return $"prior-head: {prior.Head ?? "unknown"}";
        yield return $"findings: {prior.FindingsPath}";
        foreach (ReviewFinding finding in recheck)
            yield return $"recheck: {finding.Id} ({SeverityName(finding.Severity)}) {finding.File}:{finding.Line}";

        if (recheck.Count == 0)
            yield return "recheck: none (the prior cycle left no finding for a fix round)";

        foreach (GateCycleDecision gate in plan.Gates.Where(gate => gate.Action != GateCycleAction.NotApplicable))
            yield return $"{(gate.Action == GateCycleAction.Rerun ? "rerun" : "carry")}: {gate.GateId} ({gate.Reason})";
    }

    /// <summary>One <c>ignored:</c> line per recorded cycle that does not belong to this change, with the fact that says so.</summary>
    public static IEnumerable<string> IgnoredLines(string root, IReadOnlyList<IgnoredCycle> ignored) =>
        ignored.Select(cycle => $"ignored: {Relative(root, cycle.Record.Folder)} — {cycle.Reason}");

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="folder"/>.</summary>
    public static bool IsUnder(string folder, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                && !Path.IsPathRooted(relative);
    }

    /// <summary>
    ///     The one command that runs every gate this cycle needs and writes the complete report. In a verifier cycle the
    ///     council run runs only the gates that must run again and carries the rest, so the same line serves both modes.
    /// </summary>
    public static string GatesCommand(string root, string gatesPath, string? baseRef)
    {
        string command = $"kyber-weave review gates {Quote(root)} --council --out {Quote(gatesPath)}";
        return string.IsNullOrWhiteSpace(baseRef) ? command : $"{command} --base {Quote(baseRef.Trim())}";
    }

    private static string Quote(string value) => value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    /// <summary><c>full</c> for the first cycle, <c>verifier</c> for any later one.</summary>
    public static string Mode(ReviewCyclePlan plan) => plan.IsVerifier ? "verifier" : "full";

    /// <summary>A path relative to the repository root, with forward slashes.</summary>
    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string SeverityName(ReviewSeverity severity) => severity switch
    {
        ReviewSeverity.Critical => "critical",
        ReviewSeverity.Major => "major",
        ReviewSeverity.Nitpick => "nitpick",
        _ => "minor"
    };
}
