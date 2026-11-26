using KyberWeave.Core;
using System.Text.Json;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Application.Review;

/// <summary>
///     What <c>review cycle</c>, the council's <c>review gates</c> run and <c>review verdict</c> share: choosing the
///     task's review folder, reading the findings to re-check, and describing a cycle plan in the same lines.
/// </summary>
public static class ReviewCycleView
{
    private const string InvalidInput = "KW-REVIEW-028";

    /// <summary>
    ///     The task folder name: the given id normalised and matched to an existing folder without regard to case; with
    ///     no id, the one review folder there is, or the one named for the current branch.
    /// </summary>
    /// <param name="cycles">The cycle store that lists the task folders.</param>
    /// <param name="git">Names the current branch, so the folder named for it is chosen.</param>
    /// <param name="root">The repository root.</param>
    /// <param name="scratchpad">The <c>.agent_scratchpad</c> folder.</param>
    /// <param name="given">The id the caller gave, or null.</param>
    /// <param name="task">The folder name when one was chosen.</param>
    /// <param name="note">A line saying how the folder was chosen when the caller did not name it.</param>
    /// <param name="error">Why none could be chosen.</param>
    public static bool TryChooseTask(IReviewCycleStore cycles, IGitChangeReader git, string root, string scratchpad, string? given, out string task, out string? note, out string? error)
    {
        note = null;
        error = null;
        if (!string.IsNullOrWhiteSpace(given))
        {
            task = TaskSlug.FindFolder(cycles.TaskFolders(scratchpad), given) ?? TaskSlug.Normalise(given);
            if (task.Length > 0)
                return true;

            error = $"{InvalidInput}: --task '{given}' has no letters or digits to name a folder with.";
            return false;
        }

        IReadOnlyList<string> folders = cycles.TaskFolders(scratchpad);
        string branch = TaskSlug.Normalise(CurrentBranch(git, root));
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
    /// <param name="paths">Resolves the caller's spelling of the report path.</param>
    /// <param name="environment">Names the working directory a relative report path resolves against.</param>
    /// <param name="outPath">The path the caller gave.</param>
    /// <param name="reviewFolder">The review folder the path belongs to, when it belongs to one.</param>
    /// <param name="cycle">The cycle number the path belongs to, when it belongs to one.</param>
    public static bool TryFromOutPath(
        IPathResolver paths,
        IProcessEnvironment environment,
        string outPath,
        out string reviewFolder,
        out int cycle)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);

        reviewFolder = string.Empty;
        cycle = 0;
        if (string.IsNullOrWhiteSpace(outPath))
            return false;

        string full;
        try
        {
            full = paths.GetFullPath(outPath, environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            return false;
        }

        string? cycleFolder = Path.GetDirectoryName(full);
        string? name = cycleFolder is null ? null : Path.GetFileName(cycleFolder);
        string? parent = cycleFolder is null ? null : Path.GetDirectoryName(cycleFolder);
        if (name is null || parent is null
            || !name.StartsWith(ScratchLayout.CycleFolderPrefix, StringComparison.Ordinal)
            || !int.TryParse(name.AsSpan(ScratchLayout.CycleFolderPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out cycle)
            || cycle <= 0
            || !string.Equals(Path.GetFileName(parent), ScratchLayout.ReviewFolderName, StringComparison.Ordinal))
        {
            return false;
        }

        reviewFolder = parent;
        return true;
    }

    /// <summary>The current branch, or an empty string when git cannot say.</summary>
    public static string CurrentBranch(IGitChangeReader git, string root)
    {
        try
        {
            return git.CurrentBranch(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            return string.Empty;
        }
    }

    /// <summary>The paths the working tree changed since <paramref name="commit"/>, or null when git cannot list them.</summary>
    public static IReadOnlyList<string>? TryChangedSince(IGitChangeReader git, string root, string commit)
    {
        try
        {
            return git.ResolveSince(root, commit);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The prior cycle's findings the fix round took, which this cycle re-checks: every finding at or above
    ///     <c>review.policy.fix-level</c>.
    /// </summary>
    /// <returns>False, with a finding added to <paramref name="report"/>, when the findings cannot be read.</returns>
    public static bool TryReadRecheck(
        IFileSystem files,
        ReviewCycleRecord prior,
        ReviewPolicy policy,
        DiagnosticReport report,
        IList<ReviewFinding> recheck,
        string unreadableCode,
        Severity unreadableSeverity,
        string subject)
    {
        try
        {
            FindingsReport findings = ReviewJson.ReadFindings(files.ReadAllText(prior.FindingsPath));
            foreach (ReviewFinding finding in findings.Findings.Where(policy.FixRoundTakes))
                recheck.Add(finding);
            return true;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException or InvalidOperationException)
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
    /// <param name="paths">The seam that decides identity and containment, following links and the volume's case rule.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    /// <param name="folder">The folder to test against.</param>
    /// <param name="path">The path that may lie inside the folder.</param>
    public static bool IsUnder(IPathResolver paths, IProcessEnvironment environment, string folder, string path)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);

        string fullFolder = paths.GetFullPath(folder, environment.CurrentDirectory);
        string fullPath = paths.GetFullPath(path, environment.CurrentDirectory);
        return paths.AreSame(fullFolder, fullPath) || paths.IsWithin(fullFolder, fullPath);
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
