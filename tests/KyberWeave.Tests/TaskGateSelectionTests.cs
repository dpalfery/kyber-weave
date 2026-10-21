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
