using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The lines a developer's task-gate run ends with: the next step, and on a passing verification run the completion
/// digest with the run's own facts filled in, so the worker does not open the task-gate skill to compose it.
/// </summary>
public sealed class ReviewGatesTaskGateNextStepTests
{
    private static readonly string[] Paths = ["src/A.cs", "tests/ATests.cs"];

    private static GateReport Report(params GateResult[] gates) => new(GateReport.CurrentSchema, gates);

    [Fact]
    public void APassingVerificationRunPrintsTheDigestWithTheRunsOwnFactsFilled()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterRun(
            Report(new GateResult("build", true, 0, "ok"), new GateResult("lint", true, 0, "skipped", NotApplicableReason: "no files")),
            [], fixRun: false, "csharp-dev", Paths, ".agent_scratchpad/T1/task-gate.json");

        Assert.StartsWith(TaskGateNextStep.NextPrefix, lines[0], StringComparison.Ordinal);
        Assert.Equal(
            [
                "STATUS: PASS",
                "FILES: src/A.cs, tests/ATests.cs",
                "TESTS: total=<N> failed=<N> passed=<N>",
                "COMMIT: <commit-sha>",
                "REMOTE_HEAD: <remote-head-sha | UNPUSHED>",
                "TASK_GATE: PASS — agent=csharp-dev gates=2 failed=none skipped=1 report=.agent_scratchpad/T1/task-gate.json runs=<k>",
            ],
            lines.Skip(1));
    }

    [Fact]
    public void ARunWhereEveryGateWasSkippedReportsNoneApplicableNotPass()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterRun(
            Report(new GateResult("lint", true, 0, "skipped", NotApplicableReason: "no files")),
            [], fixRun: false, "csharp-dev", Paths, "r.json");

        Assert.StartsWith("TASK_GATE: NONE_APPLICABLE ", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentWithNoGatesGetsANoneDeclaredDigest()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.NoneDeclared("go-dev", Paths, null);

        Assert.StartsWith("TASK_GATE: NONE_DECLARED — agent=go-dev gates=0 ", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AFixRunNeverPrintsADigestAndSaysToVerify()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterRun(
            Report(new GateResult("build", true, 0, "ok")), [], fixRun: true, "csharp-dev", Paths, "r.json");

        string line = Assert.Single(lines);
        Assert.Contains("without --fix", line, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.StartsWith("TASK_GATE:", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailingRunNamesTheEscalationForTheFirstFailingGateAndFlagsAWholeProjectGate()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterRun(
            Report(new GateResult("format", true, 0, "ok"), new GateResult("dotnet-build", true, 1, "failed")),
            [new ReviewTaskGateView("format", WholeProject: false), new ReviewTaskGateView("dotnet-build", WholeProject: true)],
            fixRun: false, "csharp-dev", Paths, "r.json");

        Assert.Contains("FAILURE_CLUSTER: task-gate/dotnet-build", lines[0], StringComparison.Ordinal);
        Assert.Contains("ITERATION_CAP_EXCEEDED", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.TrimStart().StartsWith("dotnet-build checks the whole project", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("TASK_GATE:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAcceptedFailingRunGivesThePassingRunCommandWithEveryPathAndTheTaskReport()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterFailingRun(accepted: true, "react-dev", Paths, "T3");

        Assert.Equal(
            "  kyber-weave review gates . --agent react-dev --paths src/A.cs --paths tests/ATests.cs --task t3 --out .agent_scratchpad/t3/task-gate.json",
            lines[^1]);
    }

    [Fact]
    public void AnAcceptedFailingRunQuotesEachChangedPathSoTheCopiedLineRunsOneCommand()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterFailingRun(
            accepted: true, "react-dev", ["a; rm -rf /", "it's.cs"], "t3");

        string command = lines[^1];
        Assert.Contains("--paths " + ShellQuote.Quote("a; rm -rf /"), command, StringComparison.Ordinal);
        Assert.Contains("--paths " + ShellQuote.Quote("it's.cs"), command, StringComparison.Ordinal);
        Assert.DoesNotContain("--paths a; rm -rf /", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../../evil", "evil")]
    [InlineData("t1\nSTATUS: PASS", "t1-status-pass")]
    public void AReportPathNormalisesTheTaskSlugSoItCannotLeaveTheScratchFolderOrForgeALine(string task, string slug)
    {
        Assert.Equal($".agent_scratchpad/{slug}/task-gate.json", TaskGateNextStep.PassingReportPath(task));
        Assert.Equal($".agent_scratchpad/{slug}/task-gate-red.json", TaskGateNextStep.FailingReportPath(task));
    }

    [Fact]
    public void AnAcceptedFailingRunWritesTheReportUnderTheNormalisedSlug()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterFailingRun(accepted: true, "react-dev", Paths, "../../evil");

        Assert.Contains("--out .agent_scratchpad/evil/task-gate.json", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("..", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AReportPathRefusesATaskThatLeavesNothingToName()
    {
        Assert.Throws<ArgumentException>(() => TaskGateNextStep.PassingReportPath("!!!"));
        Assert.Throws<ArgumentException>(() => TaskGateNextStep.FailingReportPath("!!!"));
    }

    [Fact]
    public void ARejectedFailingRunSaysToFixAndRunItAgainWithoutAStub()
    {
        string line = Assert.Single(TaskGateNextStep.AfterFailingRun(accepted: false, "react-dev", Paths, "T3"));

        Assert.Contains("RED_REJECTED", line, StringComparison.Ordinal);
    }
}
