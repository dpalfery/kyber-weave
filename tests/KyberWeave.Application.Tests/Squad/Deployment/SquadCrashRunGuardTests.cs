using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A crash-safety run must never pass with an empty loop or an unchanged clean run, so a fixture that
/// reports nothing is refused rather than silently proving nothing.
/// </summary>
public sealed class SquadCrashRunGuardTests
{
    [Fact]
    public void AFlowThatReportsNoFaultPointIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SquadCrashRunGuard.RequireFaultPoints(SquadCrashFlow.Install, []));
    }

    [Fact]
    public void AFlowThatReportsAFaultPointIsAccepted()
    {
        SquadCrashRunGuard.RequireFaultPoints(SquadCrashFlow.Install, ["step:1"]);
    }

    [Fact]
    public void ACleanRunThatChangesNothingIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SquadCrashRunGuard.RequireCleanRunChangedTheTree(SquadCrashFlow.Update, difference: null));
    }

    [Fact]
    public void ACleanRunThatChangesSomethingIsAccepted()
    {
        SquadCrashRunGuard.RequireCleanRunChangedTheTree(
            SquadCrashFlow.Update,
            "'notes/operator.md' is unexpected");
    }
}
