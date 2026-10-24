namespace KyberWeave.Core.Review;

/// <summary>How much damage a finding does if the change ships with it.</summary>
/// <remarks>
/// Severity is impact, never certainty. A finding the reporter is only half sure of but
/// which would lose data is <see cref="Critical"/> at a low confidence — both facts are
/// recorded, and <see cref="VerdictEngine"/> uses each for what it measures.
/// </remarks>
public enum ReviewSeverity
{
    /// <summary>Bounded: clarity, convention, or a narrow edge case.</summary>
    Minor,

    /// <summary>Wrong, or failing under conditions a real user reaches.</summary>
    Major,

    /// <summary>Data loss, a security hole, corruption, or behaviour that breaks silently.</summary>
    Critical
}

/// <summary>What the review concluded about the change.</summary>
public enum ReviewVerdict
{
    /// <summary>Nothing blocking survived adjudication.</summary>
    Approve,

    /// <summary>Something must change before this can proceed.</summary>
    RequestChanges,

    /// <summary>Outside what an automated review is permitted to settle.</summary>
    NeedsHuman
}

/// <summary>How risky the change is, derived from what the review found.</summary>
/// <remarks>
/// Deliberately not a function of diff size. A twelve-line migration that drops a column
/// outranks a three-thousand-line regeneration of a generated client, and grading by size
/// gets that backwards every time.
/// </remarks>
public enum RiskGrade
{
    /// <summary>Nothing of consequence found, no reserved path touched.</summary>
    Low,

    /// <summary>Real findings, or a change reaching beyond its own module.</summary>
    Medium,

    /// <summary>Critical findings, a failed blocking gate, or a reserved path.</summary>
    High
}

/// <summary>One finding reported by a review lens.</summary>
/// <param name="Id">Lens-qualified identifier, e.g. <c>di-composition/new-in-constructor</c>.</param>
/// <param name="Lens">The lens that reported it.</param>
/// <param name="Severity">Impact if the change ships with it.</param>
/// <param name="Confidence">How certain the reporter is, from 1 to 10.</param>
/// <param name="File">Repository-relative path the finding is in.</param>
/// <param name="Line">One-based line the finding anchors to.</param>
/// <param name="Excerpt">Verbatim source the finding is about.</param>
/// <param name="Claim">One sentence stating what is wrong.</param>
/// <param name="Evidence">How the reporter knows.</param>
/// <param name="FailureScenario">Concrete conditions and the wrong behaviour that results.</param>
/// <param name="Suggestion">The specific change that fixes it.</param>
public sealed record ReviewFinding(
    string Id,
    string Lens,
    ReviewSeverity Severity,
    int Confidence,
    string File,
    int Line,
    string? Excerpt = null,
    string? Claim = null,
    string? Evidence = null,
    string? FailureScenario = null,
    string? Suggestion = null);

/// <summary>What one deterministic gate reported.</summary>
/// <param name="Id">The gate's declared identifier.</param>
/// <param name="Blocking">Whether failing it blocks the change.</param>
/// <param name="ExitCode">The runner's exit code.</param>
/// <param name="Summary">One line describing the outcome.</param>
/// <param name="DurationMilliseconds">How long the gate took.</param>
public sealed record GateResult(
    string Id,
    bool Blocking,
    int ExitCode,
    string Summary,
    long DurationMilliseconds = 0)
{
    /// <summary>Whether the gate succeeded.</summary>
    public bool Passed => ExitCode == 0;
}

/// <summary>Measured line coverage, as reported by the coverage gate.</summary>
/// <param name="FileLinePercent">Line coverage across files.</param>
/// <param name="ClassLinePercent">Line coverage across classes.</param>
public sealed record CoverageResult(double FileLinePercent, double ClassLinePercent);

/// <summary>One Cobertura report the gate suite wrote, and what it measured.</summary>
/// <param name="Path">Repository-relative path of the report, with forward slashes.</param>
/// <param name="Coverage">The line coverage the report records.</param>
public sealed record CoverageReport(string Path, CoverageResult Coverage);

/// <summary>A finding the engine removed, and the rule that removed it.</summary>
/// <param name="Finding">The finding as reported.</param>
/// <param name="Code">The <c>KW-REVIEW-*</c> rule that dropped it.</param>
/// <param name="Reason">Why it was dropped.</param>
public sealed record DroppedFinding(ReviewFinding Finding, string Code, string Reason);

/// <summary>The scope of the change under review.</summary>
/// <param name="ChangedPaths">Repository-relative paths the diff touches.</param>
/// <param name="ChangedLines">Total added and removed lines.</param>
/// <param name="PathLineCounts">Per-path added and removed lines, when the producer counted them.</param>
public sealed record ReviewScope(
    IReadOnlyList<string> ChangedPaths,
    int ChangedLines,
    IReadOnlyDictionary<string, int>? PathLineCounts = null)
{
    /// <summary>Lines that count toward the reviewable ceiling after generated churn is ignored.</summary>
    /// <remarks>
    /// Lockfiles and scratch output are machine-generated churn, not human review load, so
    /// they are excluded from the <c>KW-REVIEW-009</c> numerator while staying listed in
    /// <see cref="ChangedPaths"/> for reviewers to see. When per-path counts are present
    /// the numerator is their scoped sum; without them the even-split fallback divides with
    /// a ceiling so it overestimates toward escalation rather than letting a change slip
    /// under the ceiling.
    /// </remarks>
    public int EffectiveChangedLines => ComputeEffectiveChangedLines();

    /// <summary>Whether a repository-relative path is excluded from the size numerator.</summary>
    /// <param name="path">Repository-relative path with forward slashes.</param>
    public static bool IsExcludedPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string normalized = path.Replace('\\', '/');
        if (normalized.Equals(".agent_scratchpad", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".agent_scratchpad/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int slash = normalized.LastIndexOf('/');
        string fileName = slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        if (fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("bun.lock", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Cargo.lock", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("uv.lock", StringComparison.OrdinalIgnoreCase);
    }

    private int ComputeEffectiveChangedLines()
    {
        if (ChangedLines <= 0)
        {
            return ChangedLines;
        }

        if (PathLineCounts is not null && PathLineCounts.Count > 0)
        {
            bool complete = true;
            foreach (string path in ChangedPaths)
            {
                if (IsExcludedPath(path))
                {
                    continue;
                }

                if (!PathLineCounts.ContainsKey(path))
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                int scoped = 0;
                foreach (string path in ChangedPaths)
                {
                    if (IsExcludedPath(path))
                    {
                        continue;
                    }

                    if (PathLineCounts.TryGetValue(path, out int lines) && lines > 0)
                    {
                        scoped += lines;
                    }
                }

                return scoped;
            }

            // Incomplete per-path counts fall through to the even-split heuristic below,
            // which divides with a ceiling: overestimating errs toward escalation, never
            // toward slipping under the ceiling.
        }

        if (ChangedPaths.Count == 0)
        {
            return ChangedLines;
        }

        int included = 0;
        foreach (string path in ChangedPaths)
        {
            if (!IsExcludedPath(path))
            {
                included++;
            }
        }

        if (included == ChangedPaths.Count)
        {
            return ChangedLines;
        }

        if (included == 0)
        {
            return 0;
        }

        long evenSplit = (long)ChangedLines * included;
        return (int)((evenSplit + ChangedPaths.Count - 1) / ChangedPaths.Count);
    }
}
