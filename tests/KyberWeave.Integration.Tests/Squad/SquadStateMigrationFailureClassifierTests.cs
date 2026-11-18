using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// A state-migration crash failure is explained only when the flow, the stop point and the symptom all match, and a
/// listed point that no longer fails is reported, so a widened or stale entry cannot hide a different failure.
/// </summary>
public sealed class SquadStateMigrationFailureClassifierTests
{
    private static readonly SquadStateMigrationKnownWeakness Listed = new(
        SquadStateMigrationFlow.Project,
        "registry.json' is missing",
        [10, 11],
        "the registry entry is not written");

    [Fact]
    public void AFailureAtAListedPointWithTheListedSymptomIsExplained()
    {
        (int, string)[] failures = [(10, "x: 'registry.json' is missing"), (11, "x: 'registry.json' is missing")];

        Assert.Empty(SquadStateMigrationFailureClassifier.Unexplained(SquadStateMigrationFlow.Project, failures, [Listed]));
        Assert.Empty(SquadStateMigrationFailureClassifier.Stale(SquadStateMigrationFlow.Project, failures, [Listed]));
    }

    [Fact]
    public void TheSameSymptomAtAnotherPointStaysUnexplained()
    {
        (int, string)[] failures = [(12, "x: 'registry.json' is missing")];

        Assert.Single(SquadStateMigrationFailureClassifier.Unexplained(SquadStateMigrationFlow.Project, failures, [Listed]));
    }

    [Fact]
    public void AnotherSymptomAtAListedPointStaysUnexplained()
    {
        (int, string)[] failures = [(10, "x: 'squad.receipt.json' is missing; 'registry.json' is missing")];

        Assert.Empty(SquadStateMigrationFailureClassifier.Unexplained(SquadStateMigrationFlow.Project, failures, [Listed]));
        (int, string)[] different = [(10, "x: 'migration-ledger.json' is missing")];
        Assert.Single(SquadStateMigrationFailureClassifier.Unexplained(SquadStateMigrationFlow.Project, different, [Listed]));
    }

    [Fact]
    public void AListedPointThatNoLongerFailsIsStale()
    {
        (int, string)[] failures = [(10, "x: 'registry.json' is missing")];

        string stale = Assert.Single(SquadStateMigrationFailureClassifier.Stale(SquadStateMigrationFlow.Project, failures, [Listed]));
        Assert.Contains("operation 11", stale, StringComparison.Ordinal);
    }

    [Fact]
    public void AListedWeaknessWhoseFlowDoesNotMatchExplainsNothing()
    {
        (int, string)[] failures = [(10, "x: 'registry.json' is missing")];

        Assert.Single(SquadStateMigrationFailureClassifier.Unexplained(SquadStateMigrationFlow.Global, failures, [Listed]));
    }

    private static SquadTreeEntry File(string text) =>
        new(SquadTreeEntryKind.File, System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ComparisonReportsEveryDifferenceNotJustTheFirst()
    {
        Dictionary<string, SquadTreeEntry> expected = new()
        {
            ["a"] = File("1"),
            ["b"] = File("2"),
        };
        Dictionary<string, SquadTreeEntry> actual = new() { ["b"] = File("3"), ["c"] = File("4") };

        IReadOnlyList<string> differences = SquadTreeComparison.Differences(expected, actual);

        Assert.Equal(3, differences.Count);
        Assert.Contains("'a' is missing", differences);
        Assert.Contains("'c' is unexpected", differences);
    }
}
