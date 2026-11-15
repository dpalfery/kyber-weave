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

    /// <summary>
    /// Decision D30: a gate declares <c>{tests}</c> and receives the fully qualified names of the test
    /// classes that cover the change, so the task gate runs the tests that mean something instead of
    /// the whole product suite. The name has to be fully qualified — xUnit's <c>-class</c> matches the
    /// fully qualified name, and a short name runs zero tests and still exits zero — and it has to
    /// name a class that exists, which is what has gone wrong repeatedly in this programme.
    /// </summary>
    [Fact]
    public void TheTestsTokenExpandsToTheFullyQualifiedClassThatCoversTheChange()
    {
        TaskGatePlan plan = TaskGateSelection.Plan(
            [TestsGate()], ["src/KyberWeave.Core/Review/TaskGateSelection.cs"], null, "unix");

        PlannedTaskGate gate = Assert.Single(plan.Gates);
        string argv = string.Join(" ", gate.Run);

        Assert.DoesNotContain("{tests}", argv, StringComparison.Ordinal);
        Assert.Contains(typeof(TaskGateSelectionTests).FullName!, argv, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassesThePlanDeclaresFillTheTestsTokenEvenWhenNoTestFileExistsYet()
    {
        TaskGatePlan plan = TaskGateSelection.Plan(
            [TestsGate()], ["src/KyberWeave.Cli/Program.cs"], null, "unix", declaredClasses: ["Acme.Tests.OrdersTests", "Acme.Tests.CartTests"]);

        PlannedTaskGate gate = Assert.Single(plan.Gates);
        Assert.Contains("FullyQualifiedName~Acme.Tests.OrdersTests|FullyQualifiedName~Acme.Tests.CartTests", gate.Run);
    }

    [Fact]
    public void AnArgumentContainingBothTaskAndTestsExpandsBothTokens()
    {
        ReviewTaskGate gate = new(
            "tests-task",
            ["**/*.cs"],
            null,
            ["dotnet", "test", "--filter", "{tests}-for-{task}"],
            [],
            true,
            900);

        TaskGatePlan plan = TaskGateSelection.Plan(
            [gate], ["src/KyberWeave.Core/Review/TaskGateSelection.cs"], "my-task", "unix");

        PlannedTaskGate plannedGate = Assert.Single(plan.Gates);
        string filterArg = plannedGate.Run[3];

        Assert.DoesNotContain("{task}", filterArg, StringComparison.Ordinal);
        Assert.DoesNotContain("{tests}", filterArg, StringComparison.Ordinal);
        Assert.Contains("my-task", filterArg, StringComparison.Ordinal);
        Assert.Contains(typeof(TaskGateSelectionTests).FullName!, filterArg, StringComparison.Ordinal);
    }

    /// <summary>
    /// A change no test class covers is skipped rather than passed, and skipped rather than turned
    /// into a filter that matches nothing: the gate would exit zero on an empty filter, which reads
    /// as a suite that ran and passed.
    /// </summary>
    [Fact]
    public void AChangeNoTestClassCoversSkipsTheTestsGateRatherThanRunningAnEmptyFilter()
    {
        TaskGatePlan plan = TaskGateSelection.Plan(
            [TestsGate()], ["src/KyberWeave.Cli/Program.cs"], null, "unix");

        Assert.True(
            plan.Refusals.Count == 0,
            "Nothing mapped is a skip, not a refusal: " + string.Join("; ", plan.Refusals));
        Assert.Empty(plan.Gates);
        SkippedTaskGate skip = Assert.Single(plan.Skipped);
        Assert.Equal("tests", skip.Id);
        Assert.Contains("test", skip.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnInaccessibleOrMissingRootRefusesThePlan()
    {
        ReviewTaskGate gate = FormatGate();
        TaskGatePlan plan = TaskGateSelection.Plan([gate], Changed, null, "unix", "non_existent_directory_xyz");

        string refusal = Assert.Single(plan.Refusals);
        Assert.Contains("non_existent_directory_xyz", refusal, StringComparison.Ordinal);
        Assert.Empty(plan.Gates);
    }

    [Fact]
    public void TestClassMappingInvalidRootReturnsEmptyWithoutThrowing()
    {
        IReadOnlyList<string> resolved = TestClassMapping.Resolve("non_existent_directory_xyz", Changed);
        Assert.Empty(resolved);
    }

    [Fact]
    public void TestClassMappingMatchesDirectoryIgnoringCase()
    {
        IReadOnlyList<string> resolved = TestClassMapping.Resolve(null, ["Tests/KyberWeave.Tests/TaskGateSelectionTests.cs"]);
        Assert.Contains(typeof(TaskGateSelectionTests).FullName!, resolved);
    }

    [Fact]
    public void TestsTokenResolvesOnlyFromFilesMatchingGateInclude()
    {
        ReviewTaskGate propsGate = new(
            "props-tests",
            ["**/*.props"],
            null,
            ["dotnet", "test", "--filter", "{tests}"],
            [],
            true,
            900);

        // Changed contains both a .props file (no covering test) and a .cs file (has covering test)
        TaskGatePlan plan = TaskGateSelection.Plan(
            [propsGate],
            ["Directory.Build.props", "src/KyberWeave.Core/Review/TaskGateSelection.cs"],
            null,
            "unix");

        // propsGate matches only Directory.Build.props, which has no tests, so it skips
        Assert.Empty(plan.Gates);
        SkippedTaskGate skip = Assert.Single(plan.Skipped);
        Assert.Equal("props-tests", skip.Id);
        Assert.Contains("no test class covers", skip.Reason, StringComparison.Ordinal);
    }

    private static ReviewTaskGate TestsGate()
    {
        return new ReviewTaskGate(
            "tests",
            ["**/*.cs"],
            null,
            ["dotnet", "test", "tests/KyberWeave.Tests/KyberWeave.Tests.csproj", "-c", "Release", "--filter", "{tests}"],
            [],
            true,
            900);
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
