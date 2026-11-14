using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// <c>review gates . --agent &lt;name&gt; --task &lt;id&gt;</c>: the one command a worker runs for "run my task's tests". The
/// task's files and declared test classes come from the active plan, the report goes where the Arbiter reads the
/// evidence, and the run prints a short result instead of the raw output. The gate is a shell script that prints a
/// test runner's or compiler's output, so the cases do not build a project.
/// </summary>
public sealed class ReviewGatesTaskModeTests : IDisposable
{
    private readonly DocFixture _fixture = Ready();

    public void Dispose() => _fixture.Dispose();

    private static DocFixture Ready()
    {
        DocFixture fixture = PlanTaskFileProbe.Corpus();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        new DocsTasksCommand().Execute(
            null!,
            new DocsTasksSettings { Path = Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath), Format = "json", Write = true },
            CancellationToken.None);
        return fixture;
    }

    private void Gate(string output, int exitCode)
    {
        _fixture.Write("runner-output.txt", output);
        _fixture.Write(".kyber-weave/kyber-weave.yml", $$"""
            review:
              task-gates:
                csharp-dev:
                  - id: tests
                    include: ["**/*.cs"]
                    run: [bash, -c, 'printf "%s" "$0" > filter-seen.txt; cat runner-output.txt; exit {{exitCode}}', "{tests}", dotnet, test]
            """);
    }

    private (int Exit, string Output) Run(params string[] options)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", _fixture.Root, "--agent", "csharp-dev", "--task", "T1", .. options]));
        return (execution.Result, execution.Output);
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("The fake gate is a bash script.");
    }

    [Fact]
    public void ThePassingRunTakesTheTasksFilesAndDeclaredClassesFromThePlanAndWritesThePassingReport()
    {
        SkipOnWindows();
        Gate("Passed!  - Failed:     0, Passed:    5, Skipped:     0, Total:    5, Duration: 1 s", 0);

        (int exit, string output) = Run();

        Assert.True(exit == 0, output);
        Assert.Equal("FullyQualifiedName~PlanTaskFileValidatorTests", File.ReadAllText(Path.Combine(_fixture.Root, "filter-seen.txt")));
        Assert.Contains("tests: PASS — tests: total=5 failed=0 passed=5", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fixture.Root, ".agent_scratchpad/t1/task-gate.json")), output);
        Assert.True(File.Exists(Path.Combine(_fixture.Root, ".agent_scratchpad/t1/logs/tests.log")), output);
        Assert.Contains("FILES: " + string.Join(", ", PlanTaskFileProbe.FirstTaskFiles), output, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingRunPrintsEachFailingTestShortAndKeepsTheFullOutputInTheLog()
    {
        SkipOnWindows();
        string runner = string.Join('\n',
            "  Failed Acme.Tests.OrdersTests.Total_IsTheSum [3 ms]",
            "  Error Message:",
            "   Assert.Equal() Failure: Values differ",
            new string('#', 30_000),
            "Failed!  - Failed:     1, Passed:    4, Skipped:     0, Total:    5, Duration: 1 s");
        Gate(runner, 1);

        (int exit, string output) = Run();

        Assert.Equal(1, exit);
        Assert.Contains("  Acme.Tests.OrdersTests.Total_IsTheSum — Assert.Equal() Failure: Values differ", output, StringComparison.Ordinal);
        Assert.Contains("full output: .agent_scratchpad/t1/logs/tests.log", output, StringComparison.Ordinal);
        Assert.True(output.Length < 6_000, $"{output.Length} characters printed");
        Assert.Contains(new string('#', 30_000), File.ReadAllText(Path.Combine(_fixture.Root, ".agent_scratchpad/t1/logs/tests.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void ACompileFailureOnSymbolsTheTaskHasNotWrittenIsTheFailingRunAndWritesTheFailingReport()
    {
        SkipOnWindows();
        // The test file the Test contract names is the task's own even though the task's file list leaves it out.
        string ownFile = PlanTaskFileProbe.SecondTaskFiles[0];
        Gate($"/repo/{ownFile}(12,17): error CS0246: The type or namespace name 'PlanTaskFileShape' could not be found [/repo/x.csproj]\nBuild FAILED.", 1);

        (int exit, string output) = Run("--red");

        Assert.True(exit == 0, output);
        Assert.Contains("tests: FAIL — the build failed with 1 compile error, so no test ran", output, StringComparison.Ordinal);
        Assert.Contains("RED_EVIDENCE: ", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_fixture.Root, ".agent_scratchpad/t1/task-gate-red.json")), output);
        Assert.False(File.Exists(Path.Combine(_fixture.Root, ".agent_scratchpad/t1/task-gate.json")), output);
        Assert.Contains("  kyber-weave review gates . --agent csharp-dev --task t1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFailingRunThenThePassingRunOnTheDefaultsReadAsAFailingRunFirst()
    {
        SkipOnWindows();
        Gate("  Failed Acme.Tests.T [1 ms]\n  Error Message:\n   Assert.True() Failure\nFailed!  - Failed:     1, Passed:    0, Skipped:     0, Total:    1, Duration: 1 s", 1);
        (int redExit, string redOutput) = Run("--red");
        Assert.True(redExit == 0, redOutput);

        Gate("Passed!  - Failed:     0, Passed:    1, Skipped:     0, Total:    1, Duration: 1 s", 0);
        (int passExit, string passOutput) = Run();
        Assert.True(passExit == 0, passOutput);

        string red = Path.Combine(_fixture.Root, ".agent_scratchpad/t1/task-gate-red.json");
        string pass = Path.Combine(_fixture.Root, ".agent_scratchpad/t1/task-gate.json");
        File.SetLastWriteTimeUtc(pass, File.GetLastWriteTimeUtc(red).AddSeconds(1));
        string digest = $"TASK_GATE: PASS — agent=csharp-dev gates=1 failed=none skipped=0 report={pass} runs=2";
        Assert.True(GateReportFacts.FailingRunFirst(digest, _fixture.Root));
    }

    [Fact]
    public void ATaskNoActivePlanHasSaysWhyTheLookupFailed()
    {
        SkipOnWindows();
        Gate("Passed!  - Failed:     0, Passed:    1, Skipped:     0, Total:    1, Duration: 1 s", 0);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", _fixture.Root, "--agent", "csharp-dev", "--task", "T9"]));

        // The fixture's .git is empty, so the git fallback is refused too; either way the reason the plan lookup failed is printed.
        Assert.Contains("'T9' names no one task in", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AllTestsPassingIsNotAFailingRun()
    {
        SkipOnWindows();
        Gate("Passed!  - Failed:     0, Passed:    5, Skipped:     0, Total:    5, Duration: 1 s", 0);

        (int exit, string output) = Run("--red");

        Assert.Equal(1, exit);
        Assert.Contains("RED_REJECTED: every test passed", output, StringComparison.Ordinal);
    }
}
