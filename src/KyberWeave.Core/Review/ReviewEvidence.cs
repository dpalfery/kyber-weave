using System.ComponentModel;
using System.Globalization;
using System.Text;
using KyberWeave.Core.Configuration;

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
    /// <param name="gates">The complete gate report, carried gates included, or null while the gates are still running.</param>
    /// <param name="gatesPath">Where the gate report is written.</param>
    /// <param name="priorHead">
    ///     In a verifier cycle, the head the prior cycle reviewed: the diff is then the fix delta from it, while the
    ///     changed files stay the whole change's.
    /// </param>
    /// <param name="coverageFloor">The host's <c>review.coverage</c> floor, so no seat has to look it up.</param>
    /// <returns>The evidence file's absolute path.</returns>
    /// <exception cref="IOException">A file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be written.</exception>
    public static string Write(
        string cycleFolder,
        string tree,
        string baseRef,
        GateReport? gates,
        string gatesPath,
        string? priorHead = null,
        ReviewCoverage? coverageFloor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatesPath);

        Directory.CreateDirectory(cycleFolder);
        string diffPath = Path.Combine(cycleFolder, DiffFileName);
        ReviewChange change;
        try
        {
            (string mergeBase, string patch) = ChangedFiles.DiffSinceMergeBase(tree, baseRef);
            string? deltaSince = FixDelta(tree, priorHead, ref patch);
            WriteAtomically(diffPath, patch);
            change = new ReviewChange(
                mergeBase,
                ChangedFiles.HeadCommit(tree),
                ChangedFiles.ResolveSinceMergeBase(tree, baseRef),
                null,
                deltaSince)
            {
                DiffLines = DiffLineRanges(patch)
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            change = new ReviewChange(null, null, [], ex.Message.Trim());
        }

        string path = Path.Combine(cycleFolder, FileName);
        WriteAtomically(
            path, Format(tree, baseRef, change, gates, diffPath, Path.GetFullPath(gatesPath), coverageFloor));
        return path;
    }

    /// <summary>
    ///     Each file's first and last line in the patch, so a seat reads only the hunks its lens needs instead of the
    ///     whole diff.
    /// </summary>
    /// <param name="patch">The unified diff git wrote.</param>
    public static IReadOnlyDictionary<string, (int First, int Last)> DiffLineRanges(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        Dictionary<string, (int First, int Last)> ranges = new(StringComparer.Ordinal);
        string[] lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        string? current = null;
        int first = 0;
        for (int index = 0; index < count; index++)
        {
            if (!lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
                continue;

            if (current is not null)
                ranges[current] = (first, index);

            current = PathOfHeader(lines[index]);
            first = index + 1;
        }

        if (current is not null)
            ranges[current] = (first, count);

        return ranges;
    }

    /// <summary>The new-side path of a <c>diff --git a/x b/y</c> header line.</summary>
    private static string? PathOfHeader(string header)
    {
        int split = header.LastIndexOf(" b/", StringComparison.Ordinal);
        return split < 0 ? null : header[(split + 3)..].Trim().Trim('"');
    }

    /// <summary>
    ///     Replaces the file in one rename, so a seat reading the evidence while the gate run rewrites it never sees
    ///     half a file.
    /// </summary>
    private static void WriteAtomically(string path, string text)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The evidence file's text.</summary>
    /// <param name="tree">The work tree under review.</param>
    /// <param name="baseRef">The ref the change is measured from.</param>
    /// <param name="change">What git said changed, or why it could not say.</param>
    /// <param name="gates">The complete gate report, carried gates included.</param>
    /// <param name="diffPath">The diff's absolute path.</param>
    /// <param name="gatesPath">The gate report's absolute path.</param>
    /// <param name="coverageFloor">The host's <c>review.coverage</c> floor, or null when none is known.</param>
    public static string Format(
        string tree,
        string baseRef,
        ReviewChange change,
        GateReport? gates,
        string diffPath,
        string gatesPath,
        ReviewCoverage? coverageFloor = null)
    {
        ArgumentNullException.ThrowIfNull(change);

        StringBuilder text = new();
        text.AppendLine("# Review evidence");
        text.AppendLine();
        text.AppendLine("Written by `kyber-weave review seats` and the council's `kyber-weave review gates` run. Start from it: do not run these gates again or rediscover the change. Read only the diff lines of the files your lens needs.");
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
            string scope = change.FixDeltaSince is null
                ? "merge base to the files on disk; untracked files are not in it and are new in full"
                : $"the fix delta: the prior cycle's head {change.FixDeltaSince} to the files on disk; the changed files below are the whole change";
            text.AppendLine(CultureInfo.InvariantCulture, $"- Diff: {diffPath} ({scope})");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"- Gate report: {gatesPath}");
        text.AppendLine();

        if (gates is null)
        {
            text.AppendLine("## Gates");
            text.AppendLine();
            text.AppendLine("running: the council's gate run rewrites this file with every gate's result when it finishes. "
                            + "The lenses that read a gate's output are seated after that.");
        }
        else
        {
            AppendGates(text, gates, coverageFloor);
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"## Changed files ({change.Paths.Count})");
        text.AppendLine();
        foreach (ChangedPath changed in change.Paths)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {KindName(changed.Kind)} {changed.Path}{Lines(change, changed)}");

        return text.ToString();
    }

    private static string Lines(ReviewChange change, ChangedPath changed) =>
        change.DiffLines.TryGetValue(changed.Path, out (int First, int Last) range)
            ? string.Create(CultureInfo.InvariantCulture, $" (diff lines {range.First}-{range.Last})")
            : changed.Kind == ChangeKind.Untracked ? " (not in the diff: read the file)" : string.Empty;

    private static void AppendGates(StringBuilder text, GateReport gates, ReviewCoverage? coverageFloor)
    {
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
        string measured = gates.Coverage is { } coverage
            ? string.Create(CultureInfo.InvariantCulture, $"file lines {coverage.FileLinePercent:0.#}%, class lines {coverage.ClassLinePercent:0.#}%")
            : "none collected";
        string floor = coverageFloor is { } declared && (declared.FileLinePercent > 0 || declared.ClassLinePercent > 0)
            ? string.Create(CultureInfo.InvariantCulture, $"file lines {declared.FileLinePercent:0.#}%, class lines {declared.ClassLinePercent:0.#}%")
            : "none declared";
        text.AppendLine(CultureInfo.InvariantCulture, $"Coverage: {measured}. Floor (review.coverage): {floor}.");
    }

    /// <summary>
    ///     Replaces <paramref name="patch"/> with the fix delta from <paramref name="priorHead"/>. A prior head git no
    ///     longer knows, as after a rebase, keeps the whole change's diff.
    /// </summary>
    /// <returns>The prior head the delta starts from, or null when the patch is the whole change.</returns>
    private static string? FixDelta(string tree, string? priorHead, ref string patch)
    {
        if (string.IsNullOrWhiteSpace(priorHead))
            return null;

        try
        {
            patch = ChangedFiles.DiffSince(tree, priorHead);
            return priorHead;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
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
/// <param name="FixDeltaSince">In a verifier cycle, the prior head the diff starts from; null for the whole change.</param>
public sealed record ReviewChange(
    string? MergeBase, string? Head, IReadOnlyList<ChangedPath> Paths, string? Unknown, string? FixDeltaSince = null)
{
    /// <summary>Each changed file's first and last line in the diff file, by path.</summary>
    public IReadOnlyDictionary<string, (int First, int Last)> DiffLines { get; init; } =
        new Dictionary<string, (int First, int Last)>(StringComparer.Ordinal);
}
