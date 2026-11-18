using KyberWeave.Core.Arbiter.Plans;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A dispatch that names a task id, or the command that prints its packet, tells the worker nothing about a plan:
/// KW-ARB-PLANREAD-001 and KW-DOC-TASKS-005 must not fire on it.
/// </summary>
public sealed class PlanReadPacketCommandTests
{
    private static readonly IReadOnlyList<string> PlanningDirectories = ["docs/plans/**", "docs/specs/**"];

    [Theory]
    [InlineData("Task: T1\nRun `kyber-weave docs packet T1` first and follow what it prints.")]
    [InlineData("Task T2. Your first action is `kyber-weave docs packet T2`; read what it prints and do that.")]
    [InlineData("Run `kyber-weave docs packet T3 --plan docs/plans/2026-11-13-x.md --prior-results docs/plans/r.txt` first.")]
    [InlineData("Run kyber-weave docs packet T3 --plan docs/plans/2026-11-13-x.md first.")]
    public void ADispatchNamingATaskIdOrThePacketCommandIsNotAPlanRead(string dispatch)
    {
        Assert.Empty(PlanReadInstruction.NamesPathUnder(dispatch, PlanningDirectories));
        Assert.Empty(PlanReadInstruction.Find(dispatch, PlanningDirectories));
    }

    [Theory]
    [InlineData("Run `kyber-weave docs packet T1`, then open docs/plans/2026-11-13-x.md.")]
    [InlineData("Run kyber-weave docs packet T1 then read the plan.")]
    [InlineData("Task T1: read docs/plans/2026-11-13-x.md")]
    public void APlanReadBesideThePacketCommandIsStillOne(string dispatch) =>
        Assert.True(
            PlanReadInstruction.NamesPathUnder(dispatch, PlanningDirectories).Count > 0
            || PlanReadInstruction.IsPresent(dispatch, PlanningDirectories));
}
