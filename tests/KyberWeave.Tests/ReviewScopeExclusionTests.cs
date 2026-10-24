using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// RED (issue #386, T3): <c>KW-REVIEW-009</c> counts the full <c>ChangedLines</c>
/// numerator, so generated churn — lockfiles and scratch output — can force a size
/// escalation that buries real source changes. The scope fix excludes those paths from
/// the <c>max-reviewable-lines</c> numerator while <c>ChangedPaths</c> still lists them,
/// so reviewers keep seeing lockfile bumps. Every exclusion test below fails today
/// because <see cref="VerdictEngine"/> weighs the full count; the overflow guard passes
/// today and pins that scoping never becomes a way to slip under the ceiling.
/// </summary>
/// <remarks>
/// <para>
/// Exclusion set: <c>*.lock</c>, <c>package-lock.json</c>, <c>pnpm-lock.yaml</c>,
/// <c>yarn.lock</c>, <c>bun.lock*</c>, <c>uv.lock</c>, <c>Cargo.lock</c>, and
/// <c>.agent_scratchpad/**</c>.
/// </para>
/// <para>
/// The RED cases use a 10,000-line ceiling — the <see cref="ReviewPolicy"/> default and
/// the same boundary <c>ReviewVerdictTests</c> pins at 10,001 — so the unscoped 18,200
/// total escalates today while the scoped ~200 approves. The host configures 20,000;
/// the exclusion is ceiling-independent, and the overflow guard uses that documented
/// 20,000 ceiling directly.
/// </para>
/// </remarks>
public sealed class ReviewScopeExclusionTests
{
    private static readonly DateOnly Today = new(2026, 8, 20);

    private const int SourceOnlyLines = 200;
    private const int ExcludedLines = 18_000;

    private static ReviewConfig ConfigWithCeiling(int maxReviewableLines)
    {
        return new ReviewConfig { Policy = new ReviewPolicy { MaxReviewableLines = maxReviewableLines } };
    }

    private static ReviewOutcome Evaluate(ReviewScope scope, ReviewConfig config)
    {
        return VerdictEngine.Evaluate(scope, [], [], config, Today);
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

    /// <summary>
    /// RED: 18k lockfile lines plus 200 source lines scopes to ~200, which approves
    /// under the ceiling. Fails today — the full 18,200 is weighed and escalates.
    /// </summary>
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

        ReviewOutcome outcome = Evaluate(scope, ConfigWithCeiling(10_000));

        Assert.Equal(SourceOnlyLines, scope.EffectiveChangedLines);
        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == VerdictEngine.ChangeTooLarge);
        Assert.Contains(lockfile, scope.ChangedPaths);
    }

    /// <summary>
    /// RED: 18k scratch-output lines plus 200 source lines scopes to ~200, which
    /// approves under the ceiling. Fails today — the full 18,200 is weighed and escalates.
    /// </summary>
    [Theory]
    [InlineData(".agent_scratchpad/notes.md")]
    [InlineData(".agent_scratchpad/task-1/artifacts/build.log")]
    public void ScratchpadLinesDoNotCountTowardTheSizeCeiling(string scratchPath)
    {
        ReviewScope scope = ScopeWithExcludedCounts(scratchPath);

        ReviewOutcome outcome = Evaluate(scope, ConfigWithCeiling(10_000));

        Assert.Equal(SourceOnlyLines, scope.EffectiveChangedLines);
        Assert.Equal(ReviewVerdict.Approve, outcome.Verdict);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == VerdictEngine.ChangeTooLarge);
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
    /// Guard: scoping must never become a way to slip under the ceiling — 21k lines of
    /// real source still escalates under the documented 20,000 host ceiling, with the
    /// <c>KW-REVIEW-009</c> diagnostic and the size-escalation exit code.
    /// </summary>
    [Fact]
    public void SourceOnlyOverflowStillNeedsHumanReview()
    {
        Dictionary<string, int> pathLineCounts = new()
        {
            ["src/A.cs"] = 10_500,
            ["src/B.cs"] = 10_500
        };
        ReviewScope scope = new(["src/A.cs", "src/B.cs"], 21_000, pathLineCounts);

        ReviewOutcome outcome = Evaluate(scope, ConfigWithCeiling(20_000));

        Assert.Equal(ReviewVerdict.NeedsHuman, outcome.Verdict);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Contains(outcome.Diagnostics, d => d.Code == VerdictEngine.ChangeTooLarge);
    }
}
