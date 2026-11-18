using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The crash-safety suite reports a failure as a known weakness only when the flow, the fault point
/// and the symptom all match, so a new bug cannot hide behind a listed point and a point with two
/// symptoms is explained once.
/// </summary>
public sealed class SquadCrashFailureClassifierTests
{
    private static readonly SquadCrashKnownWeakness Listed = new(
        SquadCrashFlow.Update,
        "step:3",
        "the restore temporary is not removed",
        "a restore temporary is left beside the target");

    [Fact]
    public void AFailureWithAListedPointAndSymptomIsExplained()
    {
        SquadCrashFailure failure = new(
            SquadCrashFlow.Update,
            "step:3",
            "the restore temporary is not removed: '.codex/agents/two.toml.restore'");

        Assert.Empty(SquadCrashFailureClassifier.Unexplained([failure], [Listed]));
        Assert.Equal([Listed], SquadCrashFailureClassifier.Matched([failure], [Listed]));
    }

    [Fact]
    public void ADifferentSymptomAtAListedPointStaysUnexplained()
    {
        SquadCrashFailure dataLoss = new(
            SquadCrashFlow.Update,
            "step:3",
            "a file the flow must not touch changed: 'notes/operator.md' changed from 1a2b to 3c4d");

        Assert.Equal([dataLoss], SquadCrashFailureClassifier.Unexplained([dataLoss], [Listed]));
        Assert.Empty(SquadCrashFailureClassifier.Matched([dataLoss], [Listed]));
    }

    [Fact]
    public void TwoSymptomsAtOneListedPointAreEachExplained()
    {
        SquadCrashFailure first = new(
            SquadCrashFlow.Update,
            "step:3",
            "the restore temporary is not removed: '.codex/agents/two.toml.restore'");
        SquadCrashFailure second = new(
            SquadCrashFlow.Update,
            "step:3",
            "the restore temporary is not removed: '.codex/agents/conductor.toml.restore'");

        Assert.Empty(SquadCrashFailureClassifier.Unexplained([first, second], [Listed]));
        Assert.Single(SquadCrashFailureClassifier.Matched([first, second], [Listed]));
    }

    [Fact]
    public void AFailureAtAnotherFlowOrPointStaysUnexplained()
    {
        SquadCrashFailure otherFlow = new(
            SquadCrashFlow.Uninstall,
            "step:3",
            "the restore temporary is not removed");
        SquadCrashFailure otherPoint = new(
            SquadCrashFlow.Update,
            "step:4",
            "the restore temporary is not removed");

        Assert.Equal(2, SquadCrashFailureClassifier.Unexplained([otherFlow, otherPoint], [Listed]).Count);
    }
}
