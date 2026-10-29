using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="TaskGateSelection.Plan"/> is public, and it is the last decision made before a gate
/// process starts. Its inputs are checked rather than trusted: an operating system it does not know
/// fails closed, and a gate with no program to run is a refusal naming that gate, never an exception
/// that takes the other gates down with it.
/// </summary>
public sealed class TaskGateSelectionTests
{
    private static readonly string[] Changed = ["src/KyberWeave.Core/Review/PathGlob.cs"];

    [Theory]
    [InlineData("macos")]
    [InlineData("Windows")]
    [InlineData("linux")]
    [InlineData(" unix")]
    [InlineData("")]
    public void AnOperatingSystemOtherThanWindowsOrUnixFailsClosed(string currentOs)
    {
        ReviewTaskGate gate = FormatGate();

        ArgumentException ex =
            Assert.ThrowsAny<ArgumentException>(() => TaskGateSelection.Plan([gate], Changed, null, currentOs));

        Assert.Equal("currentOs", ex.ParamName);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("unix")]
    public void WindowsAndUnixAreTheOnlyHostsThatPlanAGate(string currentOs)
    {
        TaskGatePlan plan = TaskGateSelection.Plan([FormatGate()], Changed, null, currentOs);

        Assert.Empty(plan.Refusals);
        Assert.Single(plan.Gates);
    }

    [Fact]
    public void FailOnOutputCarriesFromTheDeclaredGateToThePlannedGate()
    {
        ReviewTaskGate declared = FormatGate() with { FailOnOutput = true };

        TaskGatePlan plan = TaskGateSelection.Plan([declared, FormatGate() with { Id = "other" }], Changed, null, "unix");

        Assert.True(plan.Gates[0].FailOnOutput);
        Assert.False(plan.Gates[1].FailOnOutput);
    }

    [Fact]
    public void AGateWithNoRunCommandIsRefusedByNameAndTheOtherGatesStillPlan()
    {
        ReviewTaskGate broken = new("broken-gate", ["**/*.cs"], null, [], [], true, 900);

        TaskGatePlan plan = TaskGateSelection.Plan([broken, FormatGate()], Changed, null, "unix");

        string refusal = Assert.Single(plan.Refusals);
        Assert.Contains("'broken-gate'", refusal, StringComparison.Ordinal);
        Assert.Contains("run has no command", refusal, StringComparison.Ordinal);
        Assert.Contains("run: [", refusal, StringComparison.Ordinal);
        Assert.Equal("format-whitespace", Assert.Single(plan.Gates).Id);
    }

    /// <summary>
    /// A gate that writes under <c>.agent_scratchpad/{task}</c> cannot run without a task slug:
    /// the plan records a refusal naming the gate before any process starts, so a gate never
    /// writes to a literal <c>{task}</c> directory shared by every worker. The caller starts
    /// nothing while <see cref="TaskGatePlan.Refusals"/> is non-empty.
    /// </summary>
    [Fact]
    public void ATaskPlaceholderWithoutATaskSlugRefusesTheRunByGateName()
    {
        ReviewTaskGate gate = DotnetBuildGate();

        TaskGatePlan plan = TaskGateSelection.Plan([gate], Changed, null, "unix");

        string refusal = Assert.Single(plan.Refusals);
        Assert.Contains("'dotnet-build'", refusal, StringComparison.Ordinal);
        Assert.Contains("{task}", refusal, StringComparison.Ordinal);
        Assert.Contains("--task", refusal, StringComparison.Ordinal);
        Assert.Empty(plan.Gates);
    }

    /// <summary>
    /// The same gate expanded for two task slugs writes to two different scratchpad paths with
    /// no unexpanded token left, which is what makes the per-task path an isolation boundary
    /// rather than a shared directory.
    /// </summary>
    [Fact]
    public void DifferentTaskSlugsExpandToDifferentArtifactsPaths()
    {
        ReviewTaskGate gate = DotnetBuildGate();

        TaskGatePlan alpha = TaskGateSelection.Plan([gate], Changed, "t5-alpha", "unix");
        TaskGatePlan beta = TaskGateSelection.Plan([gate], Changed, "t5-beta", "unix");

        Assert.Empty(alpha.Refusals);
        Assert.Empty(beta.Refusals);
        IReadOnlyList<string> alphaRun = Assert.Single(alpha.Gates).Run;
        IReadOnlyList<string> betaRun = Assert.Single(beta.Gates).Run;

        Assert.All(alphaRun, element => Assert.DoesNotContain("{task}", element, StringComparison.Ordinal));
        Assert.Contains(".agent_scratchpad/t5-alpha/artifacts", alphaRun);
        Assert.Contains(".agent_scratchpad/t5-beta/artifacts", betaRun);
    }

    private static ReviewTaskGate DotnetBuildGate()
    {
        return new ReviewTaskGate(
            "dotnet-build",
            ["**/*.cs"],
            null,
            [
                "dotnet", "build", "KyberWeave.sln", "-c", "Release", "--artifacts-path",
                ".agent_scratchpad/{task}/artifacts"
            ],
            [],
            true,
            900);
    }

    private static ReviewTaskGate FormatGate()
    {
        return new ReviewTaskGate(
            "format-whitespace",
            ["**/*.cs"],
            null,
            ["dotnet", "format", "KyberWeave.sln", "whitespace", "--verify-no-changes", "--include", "{changed}"],
            [],
            true,
            900);
    }
}
