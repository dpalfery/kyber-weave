using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The suppression scan's decisions, judged from diff text and file bytes: only added lines count, the three
/// forms are recognised where they are written, and a suppression passes only when it states a reason.
/// </summary>
/// <remarks>
/// The scan is pure text handling, so it is proved here rather than through the command that reads a
/// repository: the command's own tests live in <c>tests/KyberWeave.Integration.Tests</c>, and a task gate
/// runs this project only.
/// </remarks>
public sealed class SuppressionScanTests
{
    /// <summary>
    /// A diff that adds the named lines to one file, at the line numbers the hunk header gives, so a
    /// finding's line number is read from the change rather than from the line's position in the test.
    /// </summary>
    /// <summary>
    /// A diff that adds the named lines to one file starting at <paramref name="firstLine"/>, after one
    /// unchanged context line, so a finding's line number is read from the change rather than from the
    /// line's position in the test.
    /// </summary>
    private static string DiffAddingFirstLine(params string[] added) =>
        AddedDiff(3, added);

    private static string AddedDiff(int firstLine, params string[] added) =>
        "diff --git a/src/A.cs b/src/A.cs\n"
        + "--- a/src/A.cs\n"
        + "+++ b/src/A.cs\n"
        + $"@@ -1,1 +{firstLine - 1},{added.Length + 1} @@\n"
        + " existing context\n"
        + string.Concat(added.Select(line => "+" + line + "\n"));

    [Fact]
    public void AnAddedPragmaWithNoReasonIsAFindingNamingItsFileLineAndForm()
    {
        SuppressionCandidates candidates = SuppressionScan.FromDiff(
            AddedDiff(3, "#pragma warning disable CS1591"));

        SuppressionFinding finding = Assert.Single(candidates.Findings());

        Assert.Equal("src/A.cs", finding.Path);
        Assert.Equal(3, finding.Line);
        Assert.Equal("warning-disable pragma", finding.Form);
        Assert.Equal("#pragma warning disable CS1591", finding.Excerpt);
    }

    [Fact]
    public void APragmaThatStatesItsReasonOnItsOwnLineIsNotAFinding()
    {
        SuppressionCandidates candidates = SuppressionScan.FromDiff(
            AddedDiff(3, "#pragma warning disable CS1591  // reason: generated serializer names"));

        Assert.Empty(candidates.Findings());
    }

    [Fact]
    public void APragmaThatStatesItsReasonOnTheLineAboveIsNotAFinding()
    {
        SuppressionCandidates candidates = SuppressionScan.FromDiff(
            AddedDiff(3, "// reason: the SDK shapes these names", "#pragma warning disable CS1591"));

        Assert.Empty(candidates.Findings());
    }

    /// <summary>
    /// The reason is accepted on the suppression's line or the one immediately above it, so a reason
    /// separated from the suppression by a blank line is not evidence that this suppression is explained.
    /// </summary>
    [Fact]
    public void AReasonSeparatedFromTheSuppressionByAnotherLineDoesNotExcuseIt()
    {
        SuppressionCandidates candidates = SuppressionScan.FromDiff(
            AddedDiff(3, "// reason: generated code", "// (blank separator written as a comment)", "#pragma warning disable CS1591"));

        SuppressionFinding finding = Assert.Single(candidates.Findings());
        Assert.Equal(5, finding.Line);
    }

    [Fact]
    public void TheSuppressMessageAttributeAndTheNoWarnElementAreFindingsOfTheirOwnForms()
    {
        SuppressionCandidates candidates = SuppressionScan.FromDiff(
            AddedDiff(
                3,
                "[SuppressMessage(\"Style\", \"CA1062:Validate arguments\")]",
                "<NoWarn>$(NoWarn);CS1591</NoWarn>"));

        IReadOnlyList<SuppressionFinding> findings = candidates.Findings();

        Assert.Equal(2, findings.Count);
        Assert.Equal("SuppressMessage attribute", findings[0].Form);
        Assert.Equal("NoWarn element", findings[1].Form);
    }

    /// <summary>
    /// Only a line the change adds is judged: a suppression already in the file, or one the change
    /// removes, is history the scan must not re-litigate.
    /// </summary>
    [Fact]
    public void ASuppressionTheChangeRemovesIsNotAFinding()
    {
        const string RemovedOnly =
            "diff --git a/src/A.cs b/src/A.cs\n"
            + "--- a/src/A.cs\n"
            + "+++ b/src/A.cs\n"
            + "@@ -3,1 +2,0 @@\n"
            + "-#pragma warning disable CS1591\n";

        Assert.Empty(SuppressionScan.FromDiff(RemovedOnly).Findings());
    }

    [Fact]
    public void ALineThatOnlyMentionsTheWordsIsNotASuppression()
    {
        SuppressionCandidates candidates = SuppressionScan.FromDiff(
            AddedDiff(3, "// the NoWarn list and the suppress scan were reviewed", "#pragma warning restore CS1591"));

        Assert.Empty(candidates.Findings());
    }

    [Fact]
    public void AnUntrackedTextFileIsJudgedOnEveryLineAndABinaryOneAddsNothing()
    {
        SuppressionCandidates candidates = SuppressionScan.FromDiff(string.Empty);

        Assert.True(candidates.AddUntracked("src/New.cs", "class New { }\n#pragma warning disable CS1591\n"u8.ToArray()));
        Assert.False(candidates.AddUntracked("src/Image.png", [0x89, 0x00, 0x01]));

        SuppressionFinding finding = Assert.Single(candidates.Findings());
        Assert.Equal("src/New.cs", finding.Path);
        Assert.Equal(2, finding.Line);
    }
}
