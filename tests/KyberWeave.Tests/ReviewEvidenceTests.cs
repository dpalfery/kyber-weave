using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     The evidence file is written twice: first by <c>review seats</c> with the gates still running, then by the gate
///     run with every result. Each changed file carries its line range in the diff, so a seat reads only the hunks its
///     lens needs, and the coverage line answers what test-adequacy used to search the tree for.
/// </summary>
public sealed class ReviewEvidenceTests
{
    private const string Patch = """
                                 diff --git a/src/A.cs b/src/A.cs
                                 index 1..2 100644
                                 --- a/src/A.cs
                                 +++ b/src/A.cs
                                 @@ -1 +1 @@
                                 -old
                                 +new
                                 diff --git a/docs/b.md b/docs/b.md
                                 new file mode 100644
                                 --- /dev/null
                                 +++ b/docs/b.md
                                 @@ -0,0 +1 @@
                                 +text

                                 """;

    [Fact]
    public void EachFileInThePatchGetsItsFirstAndLastLine()
    {
        IReadOnlyDictionary<string, (int First, int Last)> ranges = ReviewEvidence.DiffLineRanges(Patch);

        Assert.Equal((1, 7), ranges["src/A.cs"]);
        Assert.Equal((8, 13), ranges["docs/b.md"]);
    }

    [Fact]
    public void PendingGatesSaySoAndChangedFilesCarryTheirDiffLines()
    {
        ReviewChange change = new(
            "abc",
            "def",
            [new ChangedPath("src/A.cs", ChangeKind.Modified), new ChangedPath("src/New.cs", ChangeKind.Untracked)],
            null)
        {
            DiffLines = ReviewEvidence.DiffLineRanges(Patch)
        };

        string text = ReviewEvidence.Format("/repo", "origin/main", change, null, "/c/review-diff.patch", "/c/gates.json");

        Assert.Contains("running: the council's gate run rewrites this file", text, StringComparison.Ordinal);
        Assert.Contains("- modified src/A.cs (diff lines 1-7)", text, StringComparison.Ordinal);
        Assert.Contains("- untracked src/New.cs (not in the diff: read the file)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Coverage:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FinishedGatesListEachResultAndTheCoverageAgainstTheFloor()
    {
        ReviewChange change = new("abc", "def", [new ChangedPath("src/A.cs", ChangeKind.Modified)], null);
        GateReport measured = new(GateReport.CurrentSchema, [new GateResult("test", true, 0, "ok")], new CoverageResult(81.4, 70));

        string withFloor = ReviewEvidence.Format(
            "/repo", "origin/main", change, measured, "/c/d.patch", "/c/gates.json", new ReviewCoverage(80, 0));
        string without = ReviewEvidence.Format(
            "/repo", "origin/main", change, new GateReport(GateReport.CurrentSchema, []), "/c/d.patch", "/c/gates.json");

        Assert.Contains("- test: passed", withFloor, StringComparison.Ordinal);
        Assert.Contains("Coverage: file lines 81.4%, class lines 70%. Floor (review.coverage): file lines 80%, class lines 0%.", withFloor, StringComparison.Ordinal);
        Assert.Contains("Coverage: none collected. Floor (review.coverage): none declared.", without, StringComparison.Ordinal);
    }
}
