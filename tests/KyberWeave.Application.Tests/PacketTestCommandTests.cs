using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A worker lost four attempts filling in a task-gate template by hand, so the packet prints the task's test command
/// resolved: the Test contract's runner command as written, else the agent's <c>{tests}</c> gate expanded the way the
/// gate runner expands it.
/// </summary>
public sealed class PacketTestCommandTests
{
    private static readonly string Root = Path.GetFullPath("/repo");

    private static readonly IServiceProvider Services = DocsTestCommands.Services();

    private const string Source = "src/KyberWeave.Application/Review/TaskGateSelectionService.cs";

    /// <summary>The gate planner over fakes: the test map's class is found without reading a test file.</summary>
    private static TaskGateSelectionService GateSelection() =>
        new(
            new FakeRepositoryTreeProbe { Roots = { [Root] = new RepositoryRootAccess(true) } },
            new FakeTestClassSources
            {
                SourcesByName = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TaskGateSelectionTests"] = Path.Combine(Root, "tests", "KyberWeave.Tests", "TaskGateSelectionTests.cs"),
                },
            },
            FakePathResolver.OverEmptyFileSystem(),
            new FakeProcessEnvironment(currentDirectory: Root));

    /// <summary>The task-target service over the same fake gate planner, so no path is checked on a real disk.</summary>
    private static PlanTaskTargetService Targets() =>
        new(
            Services.GetRequiredService<IPlanTaskService>(),
            GateSelection(),
            Services.GetRequiredService<IGitRepositoryFacts>(),
            new InMemoryFileSystem(),
            FakePathResolver.OverEmptyFileSystem(),
            new FakeProcessEnvironment(currentDirectory: Root));

    /// <summary>The section <c>docs packet</c> prints, over the same services the command takes from the container.</summary>
    private static string? Render(
        string repoRoot,
        KyberWeaveConfig config,
        string? agent,
        PlanTaskFileEntry entry,
        PlanContractRow? row,
        bool windows) =>
        PacketTestCommandRenderer.Render(
            repoRoot,
            config,
            agent,
            entry,
            row,
            windows,
            Targets(),
            GateSelection(),
            new InMemoryFileSystem());


    private static KyberWeaveConfig Config() => KyberWeaveConfigAssembler.FromYaml("""
        review:
          test-map:
            "src/KyberWeave.Application/Review/TaskGateSelectionService.cs":
              - KyberWeave.Tests.TaskGateSelectionTests
          task-gates:
            csharp-dev:
              - id: tests
                include: ["**/*.cs"]
                run: [bash, scripts/run-task-tests.sh, "{tests}", ".agent_scratchpad/{task}/artifacts"]
        """);

    private static readonly PlanTaskFileEntry Task = new("T1", Files: [Source], Prompt: "Task T1: x\n");

    [Fact]
    public void AGatedTaskGetsTheGateCommandAndNotASecondCommandToRunByHand()
    {
        PlanContractRow row = new("T1", "tests/X.cs", "`dotnet test --filter FullyQualifiedName~XTests`", null, null, null);

        string? text = Render(Root, Config(), "csharp-dev", Task, row, windows: false);

        Assert.NotNull(text);
        Assert.Contains("kyber-weave review gates . --agent csharp-dev --task t1", text, StringComparison.Ordinal);
        Assert.Contains("Never run the tests by hand as well", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ready to paste", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContractRowsRunnerCommandIsPrintedVerbatimWhenNoGateRunsIt()
    {
        PlanContractRow row = new("T1", "tests/X.cs", "`dotnet test --filter FullyQualifiedName~XTests`", null, null, null);

        string? text = Render(Root, Config(), "react-dev", Task, row, windows: false);

        Assert.NotNull(text);
        Assert.StartsWith("## Test command\n", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "This task's Test contract runner command, ready to paste:\n\n    dotnet test --filter FullyQualifiedName~XTests",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheSectionNamesTheOneGateCommandForTheFailingAndThePassingRunWithNothingLeftToFillIn()
    {
        string? text = Render(Root, Config(), "csharp-dev", Task, null, windows: false);

        Assert.NotNull(text);
        Assert.Contains("    kyber-weave review gates . --agent csharp-dev --task t1 --red ", text, StringComparison.Ordinal);
        Assert.Contains("    kyber-weave review gates . --agent csharp-dev --task t1          #", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestsGateIsExpandedForTheTaskExactlyAsTheGateRunnerExpandsItAndQuotedToPaste()
    {
        string? text = Render(Root, Config(), "csharp-dev", Task, null, windows: false);

        Assert.NotNull(text);
        Assert.EndsWith(
            $"    bash scripts/run-task-tests.sh FullyQualifiedName~{typeof(TaskGateSelectionTests).FullName} .agent_scratchpad/t1/artifacts",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheClassesTheContractRowNamesFillTheTestsGateEvenBeforeTheTestFilesExist()
    {
        PlanContractRow row = new(
            "T1", "tests/Acme.Tests/OrdersTests.cs", "FullyQualifiedName~Acme.Tests.OrdersTests|FullyQualifiedName~Acme.Tests.CartTests", null, null, null);
        PlanTaskFileEntry task = Task with { Files = ["src/Acme/Orders.cs", "tests/Acme.Tests/OrdersTests.cs"] };

        string? text = Render(Root, Config(), "csharp-dev", task, row, windows: false);

        Assert.NotNull(text);
        Assert.EndsWith(
            "    bash scripts/run-task-tests.sh 'FullyQualifiedName~Acme.Tests.OrdersTests|FullyQualifiedName~Acme.Tests.CartTests' .agent_scratchpad/t1/artifacts",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AFilterOfSeveralClassesIsQuotedSoTheShellKeepsThePipe()
    {
        Assert.Equal("'FullyQualifiedName~A|FullyQualifiedName~B'", PacketTestCommandRenderer.Quote("FullyQualifiedName~A|FullyQualifiedName~B", windows: false));
    }

    [Fact]
    public void NothingIsPrintedWhenNoCommandResolves()
    {
        Assert.Null(Render(Root, Config(), null, Task, null, windows: false));
        Assert.Null(Render(Root, Config(), "go-dev", Task, null, windows: false));
        Assert.Null(Render(
            Root, Config(), "csharp-dev", Task with { Files = ["docs/README.md"] }, null, windows: false));
    }
}
