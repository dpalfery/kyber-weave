using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
///     Pins how <c>review cycle</c> answers which cycle a task is in, and how <c>review verdict --prior</c> carries
///     an earlier cycle's gate results, from the cycle folders alone. Runs against a real temporary git repository,
///     because the gates to re-run are decided by what git says changed since the prior cycle's head.
/// </summary>
public sealed class ReviewCycleTests : IDisposable
{
    private const string GateNotRerun = "KW-REVIEW-049";
    private const string GateCarried = "KW-REVIEW-048";
    private const string Task = "demo-task";

    private readonly string _repo;
    private readonly TempDirectory _temp = new();

    public ReviewCycleTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        TestGitRepositoryTemplate.CreateReviewGatesCouncilRepository(_repo);
        Git("checkout", "--quiet", "-b", "feature");
        Write(".kyber-weave/kyber-weave.yml", """
                                              review:
                                                gates:
                                                  - id: build
                                                    include: ["**/*.cs"]
                                                    run: [git, --version]
                                                  - id: docs
                                                    include: ["**/*.md"]
                                                    run: [git, --version]
                                                  - id: always
                                                    run: [git, --version]
                                              """);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ATaskWithNoFinishedCycleIsInAFullFirstCycle()
    {
        (int exit, string output) = Cycle();

        Assert.Equal(0, exit);
        Assert.Contains("cycle: 1", output, StringComparison.Ordinal);
        Assert.Contains("mode: full", output, StringComparison.Ordinal);
        Assert.DoesNotContain("rerun:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleFolderWithoutAVerdictDoesNotCountAsFinished()
    {
        Directory.CreateDirectory(CycleFolder(1));
        Write($".agent_scratchpad/{Task}/review/cycle-1/head", Head());

        (_, string output) = Cycle();

        Assert.Contains("cycle: 1", output, StringComparison.Ordinal);
        Assert.Contains("mode: full", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AFinishedCycleMakesTheNextOneAVerifierCycleThatRechecksTheFindingsAtOrAboveTheFixLevelAndRerunsTouchedGates()
    {
        FinishCycle(1, [Gate("build"), Gate("docs"), Gate("always")], [Finding("a/major", ReviewSeverity.Major), Finding("a/minor", ReviewSeverity.Minor), Finding("a/nitpick", ReviewSeverity.Nitpick)]);
        Write("src/a.cs", "// fixed\n");
        Commit("fix");

        (int exit, string output) = Cycle();

        Assert.Equal(0, exit);
        Assert.Contains("cycle: 2", output, StringComparison.Ordinal);
        Assert.Contains("mode: verifier", output, StringComparison.Ordinal);
        Assert.Contains($"folder: {CycleFolder(2)}", output, StringComparison.Ordinal);
        Assert.Contains($"findings: {Path.Combine(CycleFolder(1), "findings.json")}", output, StringComparison.Ordinal);
        Assert.Contains("recheck: a/major (major) src/a.cs:1", output, StringComparison.Ordinal);
        Assert.Contains("recheck: a/minor (minor) src/a.cs:1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("a/nitpick", output, StringComparison.Ordinal);
        Assert.Contains("rerun: build (src/a.cs changed since cycle 1)", output, StringComparison.Ordinal);
        Assert.Contains("rerun: always", output, StringComparison.Ordinal);
        Assert.Contains("carry: docs (no input changed since cycle 1)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCouncilsOwnSeatFilesDoNotCountAsAChangedInput()
    {
        FinishCycle(1, [Gate("build"), Gate("docs"), Gate("always")], []);
        Write($".agent_scratchpad/{Task}/review/cycle-1/seats/correctness.md", "NO FINDINGS: none\n");

        (_, string output) = Cycle();

        Assert.Contains("carry: docs", output, StringComparison.Ordinal);
        Assert.Contains("carry: build", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AGateThatFailedIsRerunEvenWhenNoInputChanged()
    {
        FinishCycle(1, [Gate("build", exitCode: 1), Gate("docs"), Gate("always")], []);

        (_, string output) = Cycle();

        Assert.Contains("rerun: build (it did not pass in cycle 1)", output, StringComparison.Ordinal);
        Assert.Contains("carry: docs", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACarriedResultComesFromTheLatestCycleThatRanTheGate()
    {
        FinishCycle(1, [Gate("build"), Gate("docs"), Gate("always")], []);
        Write("src/a.cs", "// fix one\n");
        Commit("fix one");
        FinishCycle(2, [Gate("build"), Gate("always")], []);
        Write("src/a.cs", "// fix two\n");
        Commit("fix two");

        (_, string output) = Cycle();

        Assert.Contains("cycle: 3", output, StringComparison.Ordinal);
        Assert.Contains("rerun: build (src/a.cs changed since cycle 2)", output, StringComparison.Ordinal);
        Assert.Contains("carry: docs (no input changed since cycle 1)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentChangeSinceThePriorHeadRerunsTheDocsGate()
    {
        FinishCycle(1, [Gate("build"), Gate("docs"), Gate("always")], []);
        Write("notes/new.md", "# new\n");

        (_, string output) = Cycle();

        Assert.Contains("rerun: docs (notes/new.md changed since cycle 1)", output, StringComparison.Ordinal);
        Assert.Contains("carry: build", output, StringComparison.Ordinal);
    }

    [Fact]
    public void APriorCycleWithNoUsableHeadCannotBeTiedToTheChangeSoItIsIgnoredAndTheRunIsFull()
    {
        FinishCycle(1, [Gate("build"), Gate("docs"), Gate("always")], [], head: "not-a-commit");

        (_, string output) = Cycle();

        Assert.Contains("mode: full", output, StringComparison.Ordinal);
        Assert.Contains("ignored: .agent_scratchpad/demo-task/review/cycle-1 — it recorded no branch, base and head (cycle.json)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("carry:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleCycleFromAnotherBranchIsListedAsIgnoredAndTheTaskStartsAtCycleOne()
    {
        FinishCycle(1, [Gate("build")], [], branch: "an-earlier-runs-branch");
        FinishCycle(2, [Gate("build")], [], branch: "an-earlier-runs-branch");

        (_, string output) = Cycle();

        Assert.Contains("cycle: 1", output, StringComparison.Ordinal);
        Assert.Contains("mode: full", output, StringComparison.Ordinal);
        Assert.Contains("ignored: .agent_scratchpad/demo-task/review/cycle-1 — it reviewed branch an-earlier-runs-branch, this is feature", output, StringComparison.Ordinal);
        Assert.Contains("ignored: .agent_scratchpad/demo-task/review/cycle-2 — it reviewed branch an-earlier-runs-branch, this is feature", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonFormatCarriesTheSameAnswer()
    {
        FinishCycle(1, [Gate("build"), Gate("docs"), Gate("always")], [Finding("a/major", ReviewSeverity.Major)]);
        Write("src/a.cs", "// fixed\n");

        (_, string output) = Cycle("--format", "json");

        using JsonDocument answer = JsonDocument.Parse(output);
        JsonElement root = answer.RootElement;
        Assert.Equal(2, root.GetProperty("cycle").GetInt32());
        Assert.Equal("verifier", root.GetProperty("mode").GetString());
        Assert.Equal(["build", "always"], root.GetProperty("rerunGates").EnumerateArray().Select(id => id.GetString()).ToArray());
        Assert.Equal(["docs"], root.GetProperty("carryGates").EnumerateArray().Select(id => id.GetString()).ToArray());
        Assert.Equal(["a/major"], root.GetProperty("recheck").EnumerateArray().Select(id => id.GetString()).ToArray());
    }

    [Theory]
    [InlineData("Demo_Task")]
    [InlineData("DEMO-TASK")]
    [InlineData("demo task")]
    public void ATaskIdInAnySpellingFindsTheFolderWithoutRegardToCase(string task)
    {
        FinishCycle(1, [Gate("build")], []);

        (int exit, string output) = Cycle(task);

        Assert.Equal(0, exit);
        Assert.Contains("cycle: 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdWithNothingToNameAFolderIsRefused()
    {
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CreateApp().Run(["review", "cycle", _repo, "--task", "___"]));

        Assert.Equal(2, run.Result);
        Assert.Contains("KW-REVIEW-028", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTaskTheOnlyReviewFolderIsUsedAndSaid()
    {
        FinishCycle(1, [Gate("build")], []);

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CreateApp().Run(["review", "cycle", _repo]));

        Assert.Equal(0, run.Result);
        Assert.Contains("task: demo-task", run.Output, StringComparison.Ordinal);
        Assert.Contains("cycle: 2", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTaskSeveralFoldersAreAnErrorListingThem()
    {
        FinishCycle(1, [Gate("build")], []);
        Directory.CreateDirectory(Path.Combine(_repo, ".agent_scratchpad", "other-task", "review"));

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CreateApp().Run(["review", "cycle", _repo]));

        Assert.Equal(2, run.Result);
        Assert.Contains("demo-task, other-task", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTaskSeveralFoldersPickTheOneNamedForTheBranch()
    {
        FinishCycle(1, [Gate("build")], []);
        Directory.CreateDirectory(Path.Combine(_repo, ".agent_scratchpad", "other-task", "review"));
        Git("checkout", "-q", "-b", "Demo_Task");

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CreateApp().Run(["review", "cycle", _repo]));

        Assert.Equal(0, run.Result);
        Assert.Contains("task: demo-task", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerdictCarriesAnEarlierPassingResultForAGateThisCycleDidNotRun()
    {
        FinishCycle(1, [Gate("build"), Gate("docs"), Gate("always")], []);
        Write("src/a.cs", "// fixed\n");
        Commit("fix");
        WriteGates(2, [Gate("build"), Gate("always")]);

        (int exit, string output) = Verdict(2);

        Assert.Equal(0, exit);
        AssertDiagnostic(output, GateCarried, "Info", "docs: carried from cycle 1");
    }

    [Fact]
    public void TheVerdictRefusesWhenAGateWhoseInputsChangedWasNotRunAgain()
    {
        FinishCycle(1, [Gate("build"), Gate("docs"), Gate("always")], []);
        Write("src/a.cs", "// fixed\n");
        Commit("fix");
        WriteGates(2, [Gate("docs"), Gate("always")]);

        (int exit, string output) = Verdict(2);

        Assert.Equal(1, exit);
        AssertDiagnostic(output, GateNotRerun, "Error", "src/a.cs changed since cycle 1");
    }

    [Fact]
    public void TheVerdictDoesNotLetAGateThatFailedDropOutOfTheReport()
    {
        FinishCycle(1, [Gate("build", exitCode: 1), Gate("docs"), Gate("always")], []);
        WriteGates(2, [Gate("docs"), Gate("always")]);

        (int exit, string output) = Verdict(2);

        Assert.Equal(1, exit);
        AssertDiagnostic(output, GateNotRerun, "Error", "it did not pass in cycle 1");
    }

    [Fact]
    public void TheVerdictCarriesNothingWithoutPrior()
    {
        WriteGates(1, [Gate("docs")]);
        string folder = CycleFolder(1);
        Write("findings-in.json", ReviewJson.Write(new FindingsReport(FindingsReport.CurrentSchema, [], [], 0)));

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CreateApp().Run(
            ["review", "verdict", _repo, "--findings", Path.Combine(_repo, "findings-in.json"),
             "--gates", Path.Combine(folder, "gates.json"), "--format", "json"]));

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain(GateCarried, run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(GateNotRerun, run.Output, StringComparison.Ordinal);
    }

    private (int Exit, string Output) Cycle(params string[] options)
    {
        string[] arguments = options.Length == 1 && !options[0].StartsWith('-')
            ? ["review", "cycle", _repo, "--task", options[0]]
            : ["review", "cycle", _repo, "--task", Task, .. options];
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CreateApp().Run(arguments));
        return (run.Result, run.Output);
    }

    private (int Exit, string Output) Verdict(int cycle)
    {
        string folder = CycleFolder(cycle);
        Write("findings-in.json", ReviewJson.Write(new FindingsReport(FindingsReport.CurrentSchema, [], [], 0)));
        string[] arguments =
        [
            "review", "verdict", _repo, "--findings", Path.Combine(_repo, "findings-in.json"),
            "--gates", Path.Combine(folder, "gates.json"), "--prior", CycleFolder(cycle - 1), "--format", "json"
        ];
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CreateApp().Run(arguments));
        return (run.Result, run.Output);
    }

    private static GateResult Gate(string id, int exitCode = 0) =>
        new(id, true, exitCode, exitCode == 0 ? "ok" : "failed");

    private static ReviewFinding Finding(string id, ReviewSeverity severity) =>
        new(id, id.Split('/')[0], severity, 9, "src/a.cs", 1);

    private string CycleFolder(int number) =>
        Path.Combine(_repo, ".agent_scratchpad", Task, "review", $"cycle-{number}");

    private void WriteGates(int cycle, GateResult[] gates) =>
        Write($".agent_scratchpad/{Task}/review/cycle-{cycle}/gates.json",
            ReviewJson.Write(new GateReport(GateReport.CurrentSchema, gates)));

    /// <summary>Writes a finished cycle: its head is the repository's current commit unless one is given.</summary>
    private void FinishCycle(int number, GateResult[] gates, ReviewFinding[] findings, string? head = null, string? branch = null)
    {
        WriteGates(number, gates);
        Write($".agent_scratchpad/{Task}/review/cycle-{number}/head", head ?? Head());
        ReviewCycleIdentity current = ReviewWorkTree.IdentityOf(_repo, null)!;
        ReviewCycles.WriteIdentity(CycleFolder(number), new ReviewCycleIdentity(branch ?? current.Branch, current.Base, head ?? current.Head));
        Write(
            $".agent_scratchpad/{Task}/review/cycle-{number}/findings.json",
            ReviewJson.Write(new FindingsReport(FindingsReport.CurrentSchema, findings, [], 0)));
    }

    private string Head() => Git("rev-parse", "HEAD").Trim();

    private void Commit(string message)
    {
        Git("add", "-A");
        Git(
            "-c", "user.name=cycle", "-c", "user.email=cycle@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "-m", message);
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string Git(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = _repo,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput;
    }

    private static CommandApp CreateApp()
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.AddBranch("review", review =>
            {
                review.AddCommand<ReviewCycleCommand>("cycle");
                review.AddCommand<ReviewVerdictCommand>("verdict");
            });
        });
        return app;
    }
}
