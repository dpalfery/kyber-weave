using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A worker lost four attempts filling in a task-gate template by hand, so the packet prints the task's test command
/// resolved: the Test contract's runner command as written, else the agent's <c>{tests}</c> gate expanded the way the
/// gate runner expands it.
/// </summary>
public sealed class PacketTestCommandTests
{
    private const string Source = "src/KyberWeave.Core/Review/TaskGateSelection.cs";

    private static KyberWeaveConfig Config() => KyberWeaveConfigLoader.LoadFromYaml("""
        review:
          task-gates:
            csharp-dev:
              - id: tests
                include: ["**/*.cs"]
                run: [bash, scripts/run-task-tests.sh, "{tests}", ".agent_scratchpad/{task}/artifacts"]
        """);

    private static readonly PlanTaskFileEntry Task = new("T1", Files: [Source], Prompt: "Task T1: x\n");

    [Fact]
    public void TheContractRowsRunnerCommandIsPrintedVerbatim()
    {
        PlanContractRow row = new("T1", "tests/X.cs", "`dotnet test --filter FullyQualifiedName~XTests`", null, null, null);

        string? text = PacketTestCommand.Render(KyberWeaveTestPaths.ToolRoot, Config(), "csharp-dev", Task, row, windows: false);

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
        string? text = PacketTestCommand.Render(KyberWeaveTestPaths.ToolRoot, Config(), "csharp-dev", Task, null, windows: false);

        Assert.NotNull(text);
        Assert.Contains("    kyber-weave review gates . --agent csharp-dev --task t1 --red ", text, StringComparison.Ordinal);
        Assert.Contains("    kyber-weave review gates . --agent csharp-dev --task t1          #", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTestsGateIsExpandedForTheTaskExactlyAsTheGateRunnerExpandsItAndQuotedToPaste()
    {
        string? text = PacketTestCommand.Render(KyberWeaveTestPaths.ToolRoot, Config(), "csharp-dev", Task, null, windows: false);

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

        string? text = PacketTestCommand.Render(KyberWeaveTestPaths.ToolRoot, Config(), "csharp-dev", task, row, windows: false);

        Assert.NotNull(text);
        Assert.EndsWith(
            "    bash scripts/run-task-tests.sh 'FullyQualifiedName~Acme.Tests.OrdersTests|FullyQualifiedName~Acme.Tests.CartTests' .agent_scratchpad/t1/artifacts",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AFilterOfSeveralClassesIsQuotedSoTheShellKeepsThePipe()
    {
        Assert.Equal("'FullyQualifiedName~A|FullyQualifiedName~B'", PacketTestCommand.Quote("FullyQualifiedName~A|FullyQualifiedName~B", windows: false));
    }

    [Fact]
    public void NothingIsPrintedWhenNoCommandResolves()
    {
        Assert.Null(PacketTestCommand.Render(KyberWeaveTestPaths.ToolRoot, Config(), null, Task, null, windows: false));
        Assert.Null(PacketTestCommand.Render(KyberWeaveTestPaths.ToolRoot, Config(), "go-dev", Task, null, windows: false));
        Assert.Null(PacketTestCommand.Render(
            KyberWeaveTestPaths.ToolRoot, Config(), "csharp-dev", Task with { Files = ["docs/README.md"] }, null, windows: false));
    }
}
