using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="ITaskGateSelectionService.Plan"/> is the last decision made before a gate
/// process starts. Its inputs are checked rather than trusted: an operating system it does not know
/// fails closed, and a gate with no program to run is a refusal naming that gate, never an exception
/// that takes the other gates down with it.
/// </summary>
public sealed class TaskGateSelectionTests
{
    private static readonly string[] Changed = ["src/KyberWeave.Core/Review/PathGlob.cs"];

    private const string Service = "src/KyberWeave.Application/Review/TaskGateSelectionService.cs";
    private const string SuppressionScan = "src/KyberWeave.Application/Review/SuppressionScan.cs";

    private static readonly Dictionary<string, IReadOnlyList<string>> TestMap = new(StringComparer.Ordinal)
    {
        [Service] = ["KyberWeave.Tests.TaskGateSelectionTests"],
        [SuppressionScan] = ["KyberWeave.Tests.SuppressionScanTests"],
    };

    private static ITaskGateSelectionService GateSelection(ITestClassSources? sources = null) =>
        new TaskGateSelectionService(
            new FakeRepositoryTreeProbe(),
            sources ?? new FakeTestClassSources
            {
                SourcesByName = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TaskGateSelectionTests"] = "/repo/tests/KyberWeave.Tests/TaskGateSelectionTests.cs",
                    ["SuppressionScanTests"] = "/repo/tests/KyberWeave.Tests/SuppressionScanTests.cs",
                },
            },
            Paths(),
            Environment());

    private static FakePathResolver Paths() => FakePathResolver.OverEmptyFileSystem();

    private static FakeProcessEnvironment Environment() => new(currentDirectory: "/work");

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
            Assert.ThrowsAny<ArgumentException>(() => GateSelection().Plan([gate], Changed, null, currentOs));

        Assert.Equal("currentOs", ex.ParamName);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("unix")]
    public void WindowsAndUnixAreTheOnlyHostsThatPlanAGate(string currentOs)
    {
        TaskGatePlan plan = GateSelection().Plan([FormatGate()], Changed, null, currentOs);

        Assert.Empty(plan.Refusals);
        Assert.Single(plan.Gates);
    }

    [Fact]
    public void FailOnOutputCarriesFromTheDeclaredGateToThePlannedGate()
    {
        ReviewTaskGate declared = FormatGate() with { FailOnOutput = true };

        TaskGatePlan plan = GateSelection().Plan([declared, FormatGate() with { Id = "other" }], Changed, null, "unix");

        Assert.True(plan.Gates[0].FailOnOutput);
        Assert.False(plan.Gates[1].FailOnOutput);
    }

    [Fact]
    public void AGateWithNoRunCommandIsRefusedByNameAndTheOtherGatesStillPlan()
    {
        ReviewTaskGate broken = new("broken-gate", ["**/*.cs"], null, [], [], true, 900);

        TaskGatePlan plan = GateSelection().Plan([broken, FormatGate()], Changed, null, "unix");

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

        TaskGatePlan plan = GateSelection().Plan([gate], Changed, null, "unix");

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

        TaskGatePlan alpha = GateSelection().Plan([gate], Changed, "t5-alpha", "unix");
        TaskGatePlan beta = GateSelection().Plan([gate], Changed, "t5-beta", "unix");

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
        TaskGatePlan plan = GateSelection().Plan(
            [TestsGate()], [Service], null, "unix", testMap: TestMap);

        PlannedTaskGate gate = Assert.Single(plan.Gates);
        string argv = string.Join(" ", gate.Run);

        Assert.DoesNotContain("{tests}", argv, StringComparison.Ordinal);
        Assert.Contains(typeof(TaskGateSelectionTests).FullName!, argv, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassesThePlanDeclaresFillTheTestsTokenEvenWhenNoTestFileExistsYet()
    {
        TaskGatePlan plan = GateSelection().Plan(
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

        TaskGatePlan plan = GateSelection().Plan(
            [gate], [Service], "my-task", "unix", testMap: TestMap);

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
        TaskGatePlan plan = GateSelection().Plan(
            [TestsGate()], ["src/KyberWeave.Cli/Program.cs"], null, "unix");

        Assert.True(
            plan.Refusals.Count == 0,
            "Nothing mapped is a skip, not a refusal: " + string.Join("; ", plan.Refusals));
        Assert.Empty(plan.Gates);
        SkippedTaskGate skip = Assert.Single(plan.Skipped);
        Assert.Equal("tests", skip.Id);
        Assert.Contains("test", skip.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The suppression scan is product code a change reaches often, and the task gate runs the unit
    /// project only: a scan class whose coverage lived only in the integration project would resolve
    /// no class here and skip its tests gate, leaving the change with no executed evidence.
    /// </summary>
    [Fact]
    public void TheSuppressionScanResolvesToAUnitClass()
    {
        TaskGatePlan plan = GateSelection().Plan(
            [TestsGate()], [SuppressionScan], null, "unix", testMap: TestMap);

        PlannedTaskGate gate = Assert.Single(plan.Gates);
        Assert.Contains("KyberWeave.Tests.SuppressionScanTests", string.Join(" ", gate.Run), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInaccessibleOrMissingRootRefusesThePlan()
    {
        ReviewTaskGate gate = FormatGate();
        TaskGatePlan plan = GateSelection().Plan([gate], Changed, null, "unix", "non_existent_directory_xyz");

        string refusal = Assert.Single(plan.Refusals);
        Assert.Contains("non_existent_directory_xyz", refusal, StringComparison.Ordinal);
        Assert.Empty(plan.Gates);
    }

    [Fact]
    public void TestClassMappingInvalidRootReturnsEmptyWithoutThrowing()
    {
        IReadOnlyList<string> resolved = GateSelection(new FakeTestClassSources { Missing = true })
            .ResolveTestClasses("non_existent_directory_xyz", Changed);
        Assert.Empty(resolved);
    }

    /// <summary>
    /// The changed path's <c>tests</c> directory is matched whatever its case, and the class's namespace comes from
    /// the file's own directory when no declaration can be read. Both facts are needed: a miss, or a name with no
    /// namespace, makes <c>dotnet test --filter</c> select nothing and exit zero, which reads afterwards as a suite
    /// that ran and passed.
    /// </summary>
    [Theory]
    [InlineData("Tests/KyberWeave.Tests/Coverage.cs")]
    [InlineData("tests/KyberWeave.Tests/Coverage.cs")]
    public void TestClassMappingMatchesTheTestsDirectoryWhateverItsCaseAndDerivesTheNamespaceFromThePath(string changed)
    {
        // The tree holds the file under the real lowercase tests directory and declares no namespace, so the
        // adjudication of the name has to come from the path. The file is named Coverage.cs, not CoverageTests.cs,
        // so the class it resolves is Coverage only while the tests directory is read case-insensitively.
        const string File = "/repo/tests/KyberWeave.Tests/Coverage.cs";
        FakeTestClassSources sources = new()
        {
            SourcesByName = new Dictionary<string, string>(StringComparer.Ordinal) { ["Coverage"] = File },
        };

        IReadOnlyList<string> resolved = GateSelection(sources).ResolveTestClasses("/repo", [changed]);

        Assert.Equal("KyberWeave.Tests.Coverage", Assert.Single(resolved));
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
        TaskGatePlan plan = GateSelection().Plan(
            [propsGate],
            ["Directory.Build.props", Service],
            null,
            "unix");

        // propsGate matches only Directory.Build.props, which has no tests, so it skips
        Assert.Empty(plan.Gates);
        SkippedTaskGate skip = Assert.Single(plan.Skipped);
        Assert.Equal("props-tests", skip.Id);
        Assert.Contains("no test class covers", skip.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AValidatedPathIsRefusedOnlyUnderTheRootItsLinkEscapes()
    {
        // The same repository-relative path escapes one root and not another. A probe that answers by path alone
        // would refuse it under both, hiding a root-confusion regression that lets an escaping path reach argv.
        FakeRepositoryTreeProbe probe = new() { EscapingPaths = [("/repo/a", "link/outside.cs")] };
        ITaskGateSelectionService selection = new TaskGateSelectionService(probe, new FakeTestClassSources(), Paths(), Environment());

        Assert.NotNull(selection.ValidatePath("/repo/a", "link/outside.cs"));
        Assert.Null(selection.ValidatePath("/repo/b", "link/outside.cs"));
    }

    [Fact]
    public void TheServiceRefusesThePlanWhenTheRootIsNotAccessible()
    {
        FakeRepositoryTreeProbe probe = new()
        {
            Roots = new Dictionary<string, RepositoryRootAccess>(StringComparer.Ordinal)
            {
                ["/repo"] = new RepositoryRootAccess(true, "permission denied"),
            },
        };
        ITaskGateSelectionService selection = new TaskGateSelectionService(probe, new FakeTestClassSources(), Paths(), Environment());

        TaskGatePlan plan = selection.Plan([FormatGate()], Changed, null, "unix", "/repo");

        Assert.Contains("/repo", Assert.Single(plan.Refusals), StringComparison.Ordinal);
        Assert.Empty(plan.Gates);
    }

    [Fact]
    public void AnInaccessibleRootIsRefusedRatherThanCrashingOrSkippingTheGate()
    {
        // The physical source throws when a root exists but cannot be listed. The planning step turns that into a
        // named refusal; letting it escape crashes the run, and swallowing it into no classes skips the tests gate.
        const string Message = "The repository root '/repo' is not accessible: permission denied";
        FakeRepositoryTreeProbe probe = new() { Roots = { ["/repo"] = RepositoryRootAccess.Accessible } };
        FakeTestClassSources sources = new() { OpenFault = Message };
        ITaskGateSelectionService selection = new TaskGateSelectionService(probe, sources, Paths(), Environment());

        TaskGatePlan plan = selection.Plan([TestsGate()], Changed, null, "unix", "/repo");

        Assert.Equal(Message, Assert.Single(plan.Refusals));
        Assert.Empty(plan.Gates);
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
