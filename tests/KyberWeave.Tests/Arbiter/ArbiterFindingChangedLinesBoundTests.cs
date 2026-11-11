using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The <c>finding.changed-lines</c> fact lists the new-side lines a finding can claim the change touched.
/// It must never carry line 0, and a change too large to list must leave the fact absent rather than
/// materialising every line or returning a silently truncated list.
/// </summary>
public sealed class ArbiterFindingChangedLinesBoundTests
{
    private const string FindingChangedLines = "finding.changed-lines";
    private const string Path = "src/Service.cs";
    private static readonly int[] ExpectedLines = [3, 4];

    /// <summary>
    /// A pure deletion carries a zero new-side count at line 0; it names no line and must add none.
    /// </summary>
    [Fact]
    public void ADeletionAtLineZeroAddsNoLines()
    {
        ArbiterFactSet facts = WithFinding();
        ChangeSet change = Change(new ChangedHunk(Path, 5, 1, 0, 0), new ChangedHunk(Path, 9, 0, 3, 2));

        ArbiterFactSet result = HookGitFacts.WithFindingChangedLines(facts, change);

        Assert.Equal(ExpectedLines, ValueOf(result));
    }

    /// <summary>
    /// A hunk that claims line 0 with a positive count cannot come from git. It is not measurable, so the fact
    /// stays absent rather than listing an invalid line.
    /// </summary>
    [Fact]
    public void AHunkStartingBeforeLineOneLeavesTheFactAbsent()
    {
        ArbiterFactSet facts = WithFinding();
        ChangeSet change = Change(new ChangedHunk(Path, 0, 0, 0, 2));

        ArbiterFactSet result = HookGitFacts.WithFindingChangedLines(facts, change);

        Assert.False(result.IsPresent(FindingChangedLines), "a hunk starting before line 1 is not a measurable change.");
    }

    /// <summary>
    /// A hunk far larger than any finding can name is not expanded line by line. The fact is absent, so a rule
    /// reads the change as undecidable instead of holding a million-entry list.
    /// </summary>
    [Fact]
    public void AHunkLargerThanTheBoundLeavesTheFactAbsent()
    {
        ArbiterFactSet facts = WithFinding();
        ChangeSet change = Change(new ChangedHunk(Path, 0, 0, 1, 1_000_000));

        ArbiterFactSet result = HookGitFacts.WithFindingChangedLines(facts, change);

        Assert.False(result.IsPresent(FindingChangedLines), "a change too large to list must be absent, not truncated.");
    }

    private static ArbiterFactSet WithFinding() =>
        new ArbiterFactSet().With("finding.file", Path, ArbiterFactLabel.Asserted);

    private static ChangeSet Change(params ChangedHunk[] hunks) =>
        new(hunks.Select(hunk => new MeasuredHunk(hunk, [])).ToList(), new HashSet<string>());

    private static IEnumerable<int> ValueOf(ArbiterFactSet facts)
    {
        Assert.True(
            facts.TryGet(FindingChangedLines, out ArbiterFact? fact) && fact!.Value is not null,
            $"{FindingChangedLines} must be present for this change.");
        return Assert.IsAssignableFrom<IEnumerable<int>>(fact!.Value);
    }
}
