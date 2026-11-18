using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The crash-safety marker check reports a marker file that is missing or changed, so a flow that
/// touches operator-owned files cannot pass.
/// </summary>
public sealed class SquadCrashMarkerCheckTests
{
    [Fact]
    public void AMarkerMissingFromTheBaselineIsReportedRatherThanThrown()
    {
        SquadTreeEntry file = new(SquadTreeEntryKind.File, System.Text.Encoding.UTF8.GetBytes("note"));
        Dictionary<string, SquadTreeEntry> baseline = new(StringComparer.Ordinal) { ["notes/operator.md"] = file };

        IReadOnlyList<string> problems = SquadCrashMarkerCheck.Problems(
            ["notes/absent.md"],
            baseline,
            baseline);

        Assert.Contains("absent.md", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AChangedMarkerIsReported()
    {
        Dictionary<string, SquadTreeEntry> baseline = new(StringComparer.Ordinal)
        {
            ["notes/operator.md"] = new SquadTreeEntry(SquadTreeEntryKind.File, System.Text.Encoding.UTF8.GetBytes("note"))
        };
        Dictionary<string, SquadTreeEntry> changed = new(StringComparer.Ordinal)
        {
            ["notes/operator.md"] = new SquadTreeEntry(SquadTreeEntryKind.File, System.Text.Encoding.UTF8.GetBytes("edited"))
        };

        IReadOnlyList<string> problems = SquadCrashMarkerCheck.Problems(
            ["notes/operator.md"],
            baseline,
            changed);

        Assert.Contains("must not touch", Assert.Single(problems), StringComparison.Ordinal);
    }
}
