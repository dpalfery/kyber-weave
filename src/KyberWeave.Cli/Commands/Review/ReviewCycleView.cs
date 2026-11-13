using System.Text.Json;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     What <c>review cycle</c> and the council's <c>review gates</c> run share: choosing the task's review folder,
///     reading the findings to re-check, and describing a cycle plan in the same lines.
/// </summary>
internal static class ReviewCycleView
{
    private const string InvalidInput = "KW-REVIEW-028";
    private const string FolderPrefix = "cycle-";

    /// <summary>
    ///     The task folder name: the given id normalised and matched to an existing folder without regard to case; with
    ///     no id, the one review folder there is, or the one named for the current branch.
    /// </summary>
    /// <param name="root">The repository root.</param>
    /// <param name="scratchpad">The <c>.agent_scratchpad</c> folder.</param>
    /// <param name="given">The id the caller gave, or null.</param>
    /// <param name="task">The folder name when one was chosen.</param>
    /// <param name="note">A line saying how the folder was chosen when the caller did not name it.</param>
    /// <param name="error">Why none could be chosen.</param>
    public static bool TryChooseTask(string root, string scratchpad, string? given, out string task, out string? note, out string? error)
    {
        note = null;
        error = null;
        if (!string.IsNullOrWhiteSpace(given))
        {
            task = TaskSlug.FindFolder(scratchpad, given) ?? TaskSlug.Normalise(given);
            if (task.Length > 0)
                return true;

            error = $"{InvalidInput}: --task '{given}' has no letters or digits to name a folder with.";
            return false;
        }

        IReadOnlyList<string> folders = TaskSlug.Folders(scratchpad);
        string branch = TaskSlug.Normalise(CurrentBranch(root));
        string[] named = [.. folders.Where(folder => branch.Length > 0 && TaskSlug.Normalise(folder) == branch)];
        if (folders.Count == 1 || named.Length == 1)
        {
            task = named.Length == 1 ? named[0] : folders[0];
            note = $"task: {task} (the only review folder{(named.Length == 1 ? " for this branch" : string.Empty)}; pass --task to choose another)";
            return true;
        }

        if (folders.Count == 0 && branch.Length > 0)
        {
            task = branch;
            note = $"task: {task} (no review folder yet; named for the current branch)";
            return true;
        }

        task = string.Empty;
        error = $"{InvalidInput}: --task is needed: " + (folders.Count == 0
            ? "there is no review folder and no current branch."
            : $"several review folders exist ({string.Join(", ", folders)}).");
        return false;
    }

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

    /// <summary>The current branch, or an empty string when git cannot say.</summary>
    public static string CurrentBranch(string root)
    {
        try
        {
            return ChangedFiles.CurrentBranch(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            return string.Empty;
        }
    }

    /// <summary>The paths the working tree changed since <paramref name="commit"/>, or null when git cannot list them.</summary>
    public static IReadOnlyList<string>? TryChangedSince(string root, string commit)
    {
        try
        {
            return ChangedFiles.ResolveSince(root, commit);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The prior cycle's critical and major findings, which this cycle re-checks.</summary>
    /// <returns>False, with a finding added to <paramref name="report"/>, when the findings cannot be read.</returns>
    public static bool TryReadRecheck(
        ReviewCycleRecord prior,
        DiagnosticReport report,
        List<ReviewFinding> recheck,
        string unreadableCode,
        Severity unreadableSeverity,
        string subject)
    {
        try
        {
            FindingsReport findings = ReviewJson.ReadFindings(File.ReadAllText(prior.FindingsPath));
            recheck.AddRange(findings.Findings.Where(finding => finding.Severity >= ReviewSeverity.Major));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                unreadableCode,
                unreadableSeverity,
                $"Could not read '{prior.FindingsPath}': {ex.Message}",
                subject,
                prior.FindingsPath,
                Hint: "Compute the prior cycle's verdict again with 'review verdict --out', then ask again."));
            return false;
        }
    }

    /// <summary>The lines that describe a verifier cycle: the prior cycle, the findings to re-check and what each gate does.</summary>
    public static IEnumerable<string> VerifierLines(string root, ReviewCyclePlan plan, IReadOnlyList<ReviewFinding> recheck)
    {
        if (plan.Prior is not { } prior)
            yield break;

        yield return $"prior: {Relative(root, prior.Folder)}";
        yield return $"prior-head: {prior.Head ?? "unknown"}";
        yield return $"findings: {Relative(root, prior.FindingsPath)}";
        foreach (ReviewFinding finding in recheck)
            yield return $"recheck: {finding.Id} ({SeverityName(finding.Severity)}) {finding.File}:{finding.Line}";

        if (recheck.Count == 0)
            yield return "recheck: none (the prior cycle left no critical or major finding)";

        foreach (GateCycleDecision gate in plan.Gates.Where(gate => gate.Action != GateCycleAction.NotApplicable))
            yield return $"{(gate.Action == GateCycleAction.Rerun ? "rerun" : "carry")}: {gate.GateId} ({gate.Reason})";
    }

    /// <summary><c>full</c> for the first cycle, <c>verifier</c> for any later one.</summary>
    public static string Mode(ReviewCyclePlan plan) => plan.IsVerifier ? "verifier" : "full";

    /// <summary>A path relative to the repository root, with forward slashes.</summary>
    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string SeverityName(ReviewSeverity severity) =>
        severity == ReviewSeverity.Critical ? "critical" : "major";
}
