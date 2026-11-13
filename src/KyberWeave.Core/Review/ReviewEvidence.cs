using System.Globalization;
using System.Text;

namespace KyberWeave.Core.Review;

/// <summary>
/// The facts a review starts from, written once into the cycle folder by the council's gate run: the diff, the
/// changed files and each gate's result. Every seat and the lead reviewer read this file instead of re-running a
/// gate or rediscovering the change.
/// </summary>
public static class ReviewEvidence
{
    /// <summary>The evidence file's name in the cycle folder.</summary>
    public const string FileName = "evidence.md";

    /// <summary>The diff's file name in the cycle folder.</summary>
    public const string DiffFileName = "review-diff.patch";

    private const int SummaryLength = 160;

    /// <summary>
    /// Writes the diff and the evidence file into <paramref name="cycleFolder"/>. When git cannot name the change, the
    /// evidence file still lists the gates and says why the diff is missing.
    /// </summary>
    /// <param name="cycleFolder">The cycle folder the files are written to.</param>
    /// <param name="tree">The work tree under review.</param>
    /// <param name="baseRef">The ref the change is measured from.</param>
    /// <param name="gates">The complete gate report, carried gates included.</param>
    /// <param name="gatesPath">Where the gate report was written.</param>
    /// <returns>The evidence file's absolute path.</returns>
    /// <exception cref="IOException">A file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be written.</exception>
    public static string Write(string cycleFolder, string tree, string baseRef, GateReport gates, string gatesPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatesPath);

        Directory.CreateDirectory(cycleFolder);
        string diffPath = Path.Combine(cycleFolder, DiffFileName);
        ReviewChange change;
        try
        {
            (string mergeBase, string patch) = ChangedFiles.DiffSinceMergeBase(tree, baseRef);
            File.WriteAllText(diffPath, patch);
            change = new ReviewChange(mergeBase, ChangedFiles.HeadCommit(tree), ChangedFiles.ResolveSinceMergeBase(tree, baseRef), null);
        }
        catch (InvalidOperationException ex)
        {
            change = new ReviewChange(null, null, [], ex.Message.Trim());
        }

        string path = Path.Combine(cycleFolder, FileName);
        File.WriteAllText(path, Format(tree, baseRef, change, gates, diffPath, Path.GetFullPath(gatesPath)));
        return path;
    }

    /// <summary>The evidence file's text.</summary>
    /// <param name="tree">The work tree under review.</param>
    /// <param name="baseRef">The ref the change is measured from.</param>
    /// <param name="change">What git said changed, or why it could not say.</param>
    /// <param name="gates">The complete gate report, carried gates included.</param>
    /// <param name="diffPath">The diff's absolute path.</param>
    /// <param name="gatesPath">The gate report's absolute path.</param>
    public static string Format(string tree, string baseRef, ReviewChange change, GateReport gates, string diffPath, string gatesPath)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(gates);

        StringBuilder text = new();
        text.AppendLine("# Review evidence");
        text.AppendLine();
        text.AppendLine("Written by the council's `kyber-weave review gates` run. Start from it: do not run these gates again or rediscover the change.");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Tree: {tree}");
        if (change.Unknown is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Base: {baseRef}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Diff: none, because git could not name the change ({change.Unknown})");
        }
        else
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- Base: {baseRef} (merge base {change.MergeBase})");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Head: {change.Head}");
            text.AppendLine(CultureInfo.InvariantCulture, $"- Diff: {diffPath} (merge base to the files on disk; untracked files are not in it and are new in full)");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"- Gate report: {gatesPath}");
        text.AppendLine();

        int passed = gates.Gates.Count(gate => gate.Passed);
        int notApplicable = gates.Gates.Count(gate => gate.NotApplicable);
        int notRun = gates.Gates.Count(VerdictEngine.IsStoppedForTime);
        int failed = gates.Gates.Count - passed - notApplicable - notRun;
        text.AppendLine("## Gates");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"passed {passed}, failed {failed}, not run {notRun}, not applicable {notApplicable}");
        text.AppendLine();
        foreach (GateResult gate in gates.Gates)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {gate.Id}: {Status(gate)} — {Summary(gate)}");

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"## Changed files ({change.Paths.Count})");
        text.AppendLine();
        foreach (ChangedPath changed in change.Paths)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {KindName(changed.Kind)} {changed.Path}");

        return text.ToString();
    }

    private static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "added",
        ChangeKind.Copied => "copied",
        ChangeKind.Modified => "modified",
        ChangeKind.Renamed => "renamed",
        ChangeKind.Deleted => "deleted",
        ChangeKind.Untracked => "untracked",
        _ => "changed"
    };

    private static string Status(GateResult gate) =>
        gate.NotApplicable ? "not applicable"
        : VerdictEngine.IsStoppedForTime(gate) ? "not run"
        : gate.Passed ? "passed"
        : gate.Blocking ? "failed, blocking"
        : "failed, not blocking";

    private static string Summary(GateResult gate)
    {
        string summary = (gate.NotApplicableReason ?? gate.Summary).ReplaceLineEndings(" ").Trim();
        return summary.Length <= SummaryLength ? summary : summary[..SummaryLength] + "…";
    }
}

/// <summary>What git said the change under review is.</summary>
/// <param name="MergeBase">The merge-base commit, or null when unknown.</param>
/// <param name="Head">The head commit, or null when unknown.</param>
/// <param name="Paths">The changed paths, untracked included.</param>
/// <param name="Unknown">Why git could not name the change, or null when it could.</param>
public sealed record ReviewChange(string? MergeBase, string? Head, IReadOnlyList<ChangedPath> Paths, string? Unknown);
