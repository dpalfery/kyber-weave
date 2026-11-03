using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Scoped line counting excludes generated churn — lockfiles and scratch output — from
/// effective changed lines, while <c>ChangedPaths</c> still lists them so reviewers keep
/// seeing lockfile bumps. With size-based escalation removed, large source diffs approve
/// normally when deterministic gates and review findings are clean.
/// </summary>
/// <remarks>
/// Exclusion set: <c>*.lock</c>, <c>package-lock.json</c>, <c>pnpm-lock.yaml</c>,
/// <c>yarn.lock</c>, <c>bun.lock*</c>, <c>uv.lock</c>, <c>Cargo.lock</c>, and
/// <c>.agent_scratchpad/**</c>.
/// </remarks>
public sealed class ReviewScopeExclusionTests
{
    private static readonly DateOnly Today = new(2026, 8, 20);

    private const int SourceOnlyLines = 200;
    private const int ExcludedLines = 18_000;

    private static ReviewOutcome Evaluate()
    {
        return VerdictEngine.Evaluate([], [], new ReviewConfig(), Today);
    }

    private static ReviewScope ScopeWithExcludedCounts(string excludedPath)
    {
        Dictionary<string, int> pathLineCounts = new()
        {
            [excludedPath] = ExcludedLines,
            ["src/Foo.cs"] = SourceOnlyLines
        };
        return new ReviewScope([excludedPath, "src/Foo.cs"], ExcludedLines + SourceOnlyLines, pathLineCounts);
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("pnpm-lock.yaml")]
    [InlineData("yarn.lock")]
    [InlineData("bun.lockb")]
    [InlineData("uv.lock")]
    [InlineData("Cargo.lock")]
    [InlineData("packages/foo/poetry.lock")]
    [InlineData("frontend/pnpm-lock.yaml")]
    public void LockfileLinesDoNotCountTowardTheSizeCeiling(string lockfile)
    {
        ReviewScope scope = ScopeWithExcludedCounts(lockfile);

        ReviewOutcome outcome = Evaluate();

        Assert.Equal(SourceOnlyLines, scope.EffectiveChangedLines);
        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == "KW-REVIEW-009");
        Assert.Contains(lockfile, scope.ChangedPaths);
    }

    [Theory]
    [InlineData(".agent_scratchpad/notes.md")]
    [InlineData(".agent_scratchpad/task-1/artifacts/build.log")]
    public void ScratchpadLinesDoNotCountTowardTheSizeCeiling(string scratchPath)
    {
        ReviewScope scope = ScopeWithExcludedCounts(scratchPath);

        ReviewOutcome outcome = Evaluate();

        Assert.Equal(SourceOnlyLines, scope.EffectiveChangedLines);
        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == "KW-REVIEW-009");
        Assert.Contains(scratchPath, scope.ChangedPaths);
    }

    /// <summary>
    /// The exclusion removes lines from the numerator, never paths from the change:
    /// reviewers still see the lockfile bump and the scratch output in
    /// <c>ChangedPaths</c>.
    /// </summary>
    [Fact]
    public void ExcludedPathsRemainListedInChangedPaths()
    {
        Dictionary<string, int> pathLineCounts = new()
        {
            ["package-lock.json"] = ExcludedLines / 2,
            [".agent_scratchpad/notes.md"] = ExcludedLines / 2,
            ["src/Foo.cs"] = SourceOnlyLines
        };
        ReviewScope scope = new(
            ["package-lock.json", ".agent_scratchpad/notes.md", "src/Foo.cs"],
            ExcludedLines + SourceOnlyLines,
            pathLineCounts);

        Assert.Equal(3, scope.ChangedPaths.Count);
        Assert.Contains("package-lock.json", scope.ChangedPaths);
        Assert.Contains(".agent_scratchpad/notes.md", scope.ChangedPaths);
        Assert.Contains("src/Foo.cs", scope.ChangedPaths);
        Assert.Equal(SourceOnlyLines, scope.EffectiveChangedLines);
    }

    /// <summary>
    /// Guard: per-path counts line up across separators and case — a producer reporting
    /// <c>SRC\Foo.cs</c> still scopes <c>src/Foo.cs</c> instead of falling through to
    /// the even-split heuristic (which would weigh half the 18,200 total here).
    /// </summary>
    [Fact]
    public void PathLineCountsMatchAcrossSeparatorsAndCase()
    {
        Dictionary<string, int> pathLineCounts = new()
        {
            ["SRC\\Foo.cs"] = SourceOnlyLines,
            ["package-lock.json"] = ExcludedLines
        };
        ReviewScope scope = new(["src/Foo.cs", "package-lock.json"], ExcludedLines + SourceOnlyLines, pathLineCounts);

        Assert.Equal(SourceOnlyLines, scope.EffectiveChangedLines);
    }

    /// <summary>
    /// Guard: the <c>bun.lock*</c> exclusion stops at a boundary — <c>bun.lockdown.json</c>
    /// is a real source file, not a bun lockfile, so it still counts.
    /// </summary>
    [Fact]
    public void BunLockdownJsonIsNotABunLockfile()
    {
        Assert.False(ReviewScope.IsExcludedPath("bun.lockdown.json"));
    }

    /// <summary>
    /// Guard: without per-path counts the fallback splits <c>ChangedLines</c> evenly across
    /// paths with a ceiling, so excluded churn never pulls the numerator down. 100 lines over
    /// three paths with one excluded scopes to 67 (ceil of 200/3), not 66.
    /// </summary>
    [Fact]
    public void EvenSplitFallbackDividesWithACeilingTowardEscalation()
    {
        ReviewScope scope = new(["package-lock.json", "src/A.cs", "src/B.cs"], 100);

        Assert.Equal(67, scope.EffectiveChangedLines);
    }

    /// <summary>
    /// With size-based escalation removed, large source diffs approve normally when
    /// deterministic gates and review findings are clean.
    /// </summary>
    [Fact]
    public void SourceOnlyLargeDiffApprovesWhenGatesAndFindingsAreClean()
    {
        Dictionary<string, int> pathLineCounts = new()
        {
            ["src/A.cs"] = 10_500,
            ["src/B.cs"] = 10_500
        };
        ReviewScope scope = new(["src/A.cs", "src/B.cs"], 21_000, pathLineCounts);

        ReviewOutcome outcome = Evaluate();

        Assert.Equal(21_000, scope.EffectiveChangedLines);
        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.Equal(0, outcome.ExitCode);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == "KW-REVIEW-009");
    }
}
