using KyberWeave.Cli.Commands.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The lines a developer's task-gate run ends with: the next step, and on a passing verification run the completion
/// digest with the run's own facts filled in, so the worker does not open the task-gate skill to compose it.
/// </summary>
public sealed class ReviewGatesTaskGateNextStepTests
{
    private const string TaskFile = "tests/Widget.Tests/WidgetTests.cs";

    private static readonly string[] Paths = ["src/A.cs", "tests/ATests.cs"];

    private readonly InMemoryFileSystem _files = new();
    private readonly string _repo;

    /// <summary>Creates the in-memory repository whose task gates the run-count cases drive.</summary>
    public ReviewGatesTaskGateNextStepTests()
    {
        _repo = Path.Combine(_files.Root, "work", "repo");
        _files.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _files.CreateDirectory(Path.Combine(_repo, "tests/Widget.Tests"));
        _files.WriteAllText(Path.Combine(_repo, TaskFile), "namespace Widget.Tests; public class WidgetTests { }\n");
        _files.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            """
            review:
              task-gates:
                csharp-dev:
                  - id: tests
                    include: ["**/*.cs"]
                    run: [dotnet, test]
            """);
    }

    private static GateReport Report(params GateResult[] gates) => new(GateReport.CurrentSchema, gates);

    [Fact]
    public void APassingVerificationRunPrintsTheDigestWithTheRunsOwnFactsFilled()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterRun(
            Report(new GateResult("build", true, 0, "ok"), new GateResult("lint", true, 0, "skipped", NotApplicableReason: "no files")),
            [], fixRun: false, "csharp-dev", Paths, ".agent_scratchpad/T1/task-gate.json", runCount: 3);

        Assert.StartsWith(TaskGateNextStep.NextPrefix, lines[0], StringComparison.Ordinal);
        Assert.Equal(
            [
                "STATUS: PASS",
                "FILES: src/A.cs, tests/ATests.cs",
                "TESTS: total=<N> failed=<N> passed=<N>",
                "COMMIT: <commit-sha>",
                "REMOTE_HEAD: <remote-head-sha | UNPUSHED>",
                "TASK_GATE: PASS — agent=csharp-dev gates=2 failed=none skipped=1 report=.agent_scratchpad/T1/task-gate.json runs=3",
            ],
            lines.Skip(1));
    }

    [Fact]
    public void ARunWhereEveryGateWasSkippedReportsNoneApplicableNotPass()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterRun(
            Report(new GateResult("lint", true, 0, "skipped", NotApplicableReason: "no files")),
            [], fixRun: false, "csharp-dev", Paths, "r.json", runCount: 1);

        Assert.StartsWith("TASK_GATE: NONE_APPLICABLE ", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentWithNoGatesGetsANoneDeclaredDigest()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.NoneDeclared("go-dev", Paths, null, runCount: 1);

        Assert.StartsWith("TASK_GATE: NONE_DECLARED — agent=go-dev gates=0 ", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AFixRunNeverPrintsADigestAndSaysToVerify()
    {
        IReadOnlyList<string> lines = TaskGateNextStep.AfterRun(
            Report(new GateResult("build", true, 0, "ok")), [], fixRun: true, "csharp-dev", Paths, "r.json", runCount: 1);

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
            fixRun: false, "csharp-dev", Paths, "r.json", runCount: 1);

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
    public void AnAcceptedFailingRunQuotesTheAgentSoItCannotStartASecondCommand()
    {
        const string Evil = "a; echo pwned";
        string quoted = ShellQuote.Quote(Evil);

        IReadOnlyList<string> viaPlan = TaskGateNextStep.AfterFailingRun(accepted: true, Evil, Paths, "T3", fromPlan: true);
        Assert.Contains("--agent " + quoted, viaPlan[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("--agent " + Evil, viaPlan[^1], StringComparison.Ordinal);

        IReadOnlyList<string> viaPaths = TaskGateNextStep.AfterFailingRun(accepted: true, Evil, Paths, "T3");
        Assert.Contains("--agent " + quoted, viaPaths[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("--agent " + Evil, viaPaths[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedFailingRunSaysToFixAndRunItAgainWithoutAStub()
    {
        string line = Assert.Single(TaskGateNextStep.AfterFailingRun(accepted: false, "react-dev", Paths, "T3"));

        Assert.Contains("RED_REJECTED", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AFirstRunForATaskPrintsRunsEqualsOne()
    {
        CapturedConsoleExecution<int> run = Gates(
            PassingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile);

        Assert.Equal(0, run.Result);
        Assert.Contains("STATUS: PASS", run.Output, StringComparison.Ordinal);
        Assert.Contains("runs=1", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThirdRunForATaskPrintsRunsEqualsThree()
    {
        _ = Gates(PassingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile);
        _ = Gates(PassingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile);
        CapturedConsoleExecution<int> third = Gates(
            PassingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile);

        Assert.Equal(0, third.Result);
        Assert.Contains("runs=3", third.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWithoutATaskIdPrintsRunsEqualsOne()
    {
        CapturedConsoleExecution<int> run = Gates(
            PassingResult(), "--agent", "csharp-dev", "--paths", TaskFile);

        Assert.Equal(0, run.Result);
        Assert.Contains("runs=1", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARedLinePrintsTheSameRunCountAsTheRunItEnds()
    {
        _ = Gates(PassingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile);
        CapturedConsoleExecution<int> red = Gates(
            FailingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile, "--red");

        Assert.Equal(0, red.Result);
        Assert.Contains("RED_EVIDENCE: ", red.Output, StringComparison.Ordinal);
        Assert.Contains("runs=2", red.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableCounterCountsAsZeroAndTheRunStillReports()
    {
        string counter = Path.Combine(_repo, ".agent_scratchpad", "t1", "task-gate-runs");
        _files.CreateDirectory(Path.GetDirectoryName(counter)!);
        _files.WriteAllText(counter, "not a run count");

        CapturedConsoleExecution<int> run = Gates(
            PassingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile);

        Assert.Equal(0, run.Result);
        Assert.Contains("STATUS: PASS", run.Output, StringComparison.Ordinal);
        Assert.Contains("runs=1", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoGateRunDoesNotConsumeARunOrdinalForTheFirstGatedRun()
    {
        CapturedConsoleExecution<int> noGates = Gates(
            PassingResult(), "--agent", "go-dev", "--task", "t1", "--paths", TaskFile);

        Assert.Equal(0, noGates.Result);
        Assert.Contains("TASK_GATE: NONE_DECLARED", noGates.Output, StringComparison.Ordinal);
        Assert.Contains("runs=1", noGates.Output, StringComparison.Ordinal);

        CapturedConsoleExecution<int> gated = Gates(
            PassingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile);

        Assert.Equal(0, gated.Result);
        Assert.Contains("STATUS: PASS", gated.Output, StringComparison.Ordinal);
        Assert.Contains("runs=1", gated.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedRunAfterGateResolutionDoesNotConsumeARunOrdinal()
    {
        using (InMemoryReviewServices failing = new(_files))
        {
            failing.Processes.Script("git", new ProcessRunResult(128, string.Empty, TimedOut: false)
            {
                StandardError = "fatal: not a git repository",
            });
            CapturedConsoleExecution<int> refused = failing.Run(
                failing.ReviewGatesApp(), ["review", "gates", _repo, "--agent", "csharp-dev", "--task", "t1"]);
            Assert.Equal(1, refused.Result);
        }

        CapturedConsoleExecution<int> gated = Gates(
            PassingResult(), "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile);

        Assert.Equal(0, gated.Result);
        Assert.Contains("STATUS: PASS", gated.Output, StringComparison.Ordinal);
        Assert.Contains("runs=1", gated.Output, StringComparison.Ordinal);
    }

    private static ProcessRunResult PassingResult() => new(0, string.Empty, TimedOut: false)
    {
        StandardOutput = "Passed!  - Failed:     0, Passed:    1, Skipped:     0, Total:    1, Duration: 1 s",
    };

    private static ProcessRunResult FailingResult() => new(1, string.Empty, TimedOut: false)
    {
        StandardOutput = "  Failed Widget.Tests.WidgetTests.T [1 ms]\n  Error Message:\n   Assert.True() Failure\n" +
                         "Failed!  - Failed:     1, Passed:    0, Skipped:     0, Total:    1, Duration: 1 s",
    };

    private CapturedConsoleExecution<int> Gates(ProcessRunResult gate, params string[] options)
    {
        using InMemoryReviewServices services = new(_files);
        services.Processes.Script("dotnet", gate);
        return services.Run(services.ReviewGatesApp(), ["review", "gates", _repo, .. options]);
    }
}
