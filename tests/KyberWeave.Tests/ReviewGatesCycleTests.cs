using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins that the council's own gate run decides the review cycle: in a cycle after the first, <c>review gates
///     --council</c> prints the cycle and what to re-check, runs only the gates whose inputs changed or that did not
///     pass, and writes a complete report with the other gates' earlier passing results carried into it. Runs against a
///     real temporary git repository, because which gates to run again is decided by what git says changed.
/// </summary>
public sealed class ReviewGatesCycleTests : IDisposable
{
    private const string Task = "demo-task";
    private readonly string _repo;
    private readonly TempDirectory _temp = new();

    public ReviewGatesCycleTests()
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
    public void AFirstCycleRunsEveryGateAndPrintsAFullHeaderWithItsAbsoluteFolder()
    {
        (int exit, string output) = Gates(1, "--format", "lines");

        Assert.Equal(0, exit);
        Assert.StartsWith("cycle: 1 mode: full", output, StringComparison.Ordinal);
        Assert.Contains($"folder: {CycleFolder(1)}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored:", output, StringComparison.Ordinal);
        Assert.Equal(["build", "docs", "always"], ReportedIds(1));
        Assert.DoesNotContain("carried", ReadGates(1), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondCycleRunsOnlyTheGateTheFixTouchedAndCarriesTheRestIntoACompleteReport()
    {
        FinishCycle1();
        Write("src/a.cs", "// fixed\n");
        Commit("fix");

        (int exit, string output) = Gates(2);

        Assert.Equal(0, exit);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("cycle: 2 mode: verifier", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.StartsWith("recheck: a/major (major) src/a.cs:1", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("rerun: build (src/a.cs changed since cycle 1)", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("carry: docs (no input changed since cycle 1)", StringComparison.Ordinal));

        GateReport report = ReviewJson.ReadGates(ReadGates(2));
        Assert.Equal(["build", "docs", "always"], report.Gates.Select(gate => gate.Id));
        Assert.StartsWith("carried from cycle 1:", report.Gates.Single(gate => gate.Id == "docs").Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("carried", report.Gates.Single(gate => gate.Id == "build").Summary, StringComparison.Ordinal);
        Assert.All(report.Gates, gate => Assert.True(gate.Passed));
    }

    [Fact]
    public void TheCompleteReportYieldsAVerdictWithoutRunningTheCarriedGate()
    {
        FinishCycle1();
        Write("src/a.cs", "// fixed\n");
        Commit("fix");
        Gates(2);

        CapturedConsoleExecution<int> verdict = ProcessConsoleCapture.Run(() => CreateApp().Run(
        [
            "review", "verdict", _repo, "--findings", EmptyFindings(), "--gates", GatesPath(2),
            "--prior", CycleFolder(1), "--format", "json"
        ]));

        Assert.Equal(0, verdict.Result);
        Assert.Contains("APPROVE", verdict.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AGateThatFailedInThePriorCycleRunsAgainEvenWhenNoInputChanged()
    {
        FinishCycle1(buildExit: 1);

        (_, string output) = Gates(2);

        Assert.Contains("rerun: build (it did not pass in cycle 1)", output, StringComparison.Ordinal);
        Assert.Contains("carry: docs", output, StringComparison.Ordinal);
        Assert.Equal(["build", "docs", "always"], ReviewJson.ReadGates(ReadGates(2)).Gates.Select(gate => gate.Id));
    }

    [Fact]
    public void FullRunsEveryGateInASecondCycle()
    {
        FinishCycle1();

        (_, string output) = Gates(2, "--full");

        Assert.Contains("mode: full", output, StringComparison.Ordinal);
        Assert.DoesNotContain("mode: verifier", output, StringComparison.Ordinal);
        Assert.DoesNotContain("carried", ReadGates(2), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutCouncilTheRunIsNotTreatedAsAVerifierCycle()
    {
        FinishCycle1();

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CreateApp().Run(
            ["review", "gates", _repo, "--out", GatesPath(2)]));

        Assert.DoesNotContain("mode:", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("carried", ReadGates(2), StringComparison.Ordinal);
    }

    [Fact]
    public void AStructuredFormatStaysOneDocumentWithNoCycleHeader()
    {
        FinishCycle1();

        (_, string output) = Gates(2, "--format", "json");

        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("verifier", document.RootElement.GetProperty("metrics").GetProperty("mode").GetString());
    }

    [Fact]
    public void ACycleFolderThatIsAlreadyFinishedIsNotItsOwnPrior()
    {
        FinishCycle1();

        (_, string output) = Gates(1);

        Assert.Contains("mode: full", output, StringComparison.Ordinal);
        Assert.DoesNotContain("mode: verifier", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHeaderComesBeforeTheGateReportAndTheSummaryClosesTheOutput()
    {
        FinishCycle1();
        Write("src/a.cs", "// fixed\n");
        Commit("fix");

        (_, string output) = Gates(2);

        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int header = Array.FindIndex(lines, line => line.StartsWith("cycle: 2 mode: verifier", StringComparison.Ordinal) && !line.Contains(" ran:", StringComparison.Ordinal));
        int firstGate = Array.FindIndex(lines, line => line.Contains("KW-REVIEW-021", StringComparison.Ordinal));
        Assert.Equal(0, header);
        Assert.True(firstGate > header, "the gate lines follow the header");
        Assert.StartsWith("cycle: 2 mode: verifier ran: 2 carried: 1 failed: 0", lines[^1], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.StartsWith("rerun: build", StringComparison.Ordinal));
    }

    [Fact]
    public void ACycleRecordedForAnotherBranchIsIgnoredAndTheRunIsFull()
    {
        FinishCycle1(branch: "someone-elses-change");
        Write("src/a.cs", "// fixed\n");
        Commit("fix");

        (_, string output) = Gates(2);

        Assert.StartsWith("cycle: 1 mode: full", output, StringComparison.Ordinal);
        Assert.Contains("ignored: .agent_scratchpad/demo-task/review/cycle-1 — it reviewed branch someone-elses-change, this is feature", output, StringComparison.Ordinal);
        Assert.DoesNotContain("carry:", output, StringComparison.Ordinal);
        Assert.Equal(["build", "docs", "always"], ReportedIds(2));
        Assert.DoesNotContain("carried", ReadGates(2), StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleRecordedAgainstAnotherBaseIsIgnored()
    {
        FinishCycle1(baseCommit: OffBranchCommit());

        (_, string output) = Gates(2);

        Assert.Contains("ignored: .agent_scratchpad/demo-task/review/cycle-1 — its base ", output, StringComparison.Ordinal);
        Assert.Contains("mode: full", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleWhoseHeadIsNotAnAncestorOfHeadIsIgnored()
    {
        string stale = OffBranchCommit();
        FinishCycle1(head: stale);

        (_, string output) = Gates(2);

        Assert.Contains($"ignored: .agent_scratchpad/demo-task/review/cycle-1 — its head {stale[..12]} is not an ancestor of HEAD", output, StringComparison.Ordinal);
        Assert.Contains("mode: full", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleThatRecordedNoBranchAndBaseIsIgnored()
    {
        FinishCycle1(identity: false);

        (_, string output) = Gates(2);

        Assert.Contains("it recorded no branch, base and head (cycle.json)", output, StringComparison.Ordinal);
        Assert.Contains("mode: full", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACycleOfThisBranchWhoseHeadIsAnAncestorCountsAndIsNotListedAsIgnored()
    {
        FinishCycle1();
        Write("src/a.cs", "// fixed\n");
        Commit("fix");

        (_, string output) = Gates(2);

        Assert.Contains("mode: verifier", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGateRunRecordsTheBranchBaseAndHeadInTheCycleFolder()
    {
        Gates(1);

        using JsonDocument identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(CycleFolder(1), "cycle.json")));
        Assert.Equal("feature", identity.RootElement.GetProperty("branch").GetString());
        Assert.Equal(Git("rev-parse", "HEAD").Trim(), identity.RootElement.GetProperty("head").GetString());
        Assert.Equal(Git("rev-list", "--max-parents=0", "HEAD").Trim(), identity.RootElement.GetProperty("base").GetString());
    }

    [Fact]
    public void TheVerdictRecordsTheChangeInAFolderThatHasNoRecordYet()
    {
        Directory.CreateDirectory(CycleFolder(1));
        CapturedConsoleExecution<int> verdict = ProcessConsoleCapture.Run(() => CreateApp().Run(
            ["review", "verdict", _repo, "--findings", EmptyFindings(), "--out", Path.Combine(CycleFolder(1), "findings.json")]));

        Assert.True(File.Exists(Path.Combine(CycleFolder(1), "cycle.json")), verdict.Output);
    }

    [Fact]
    public void TheVerdictRefusesAPriorFolderThatBelongsToAnotherChange()
    {
        FinishCycle1(branch: "someone-elses-change");

        CapturedConsoleExecution<int> verdict = ProcessConsoleCapture.Run(() => CreateApp().Run(
        [
            "review", "verdict", _repo, "--findings", EmptyFindings(), "--gates", GatesPath(1),
            "--prior", CycleFolder(1)
        ]));

        Assert.NotEqual(0, verdict.Result);
        Assert.Contains("KW-REVIEW-050", verdict.Output, StringComparison.Ordinal);
        Assert.Contains("it reviewed branch someone-elses-change, this is feature", verdict.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorktreeResolvesItsReviewFolderInsideItselfAndIgnoresTheCanonicalCheckoutsStaleFolder()
    {
        // A finished cycle left in the canonical checkout by an earlier run must not be read for the worktree's run.
        FinishCycle1();
        string worktree = Path.Combine(_temp.Path, "repo.worktrees", "run");
        Git("worktree", "add", "--quiet", "-b", "wt-branch", worktree);
        Directory.CreateDirectory(Path.Combine(worktree, ".kyber-weave"));
        File.Copy(Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"), Path.Combine(worktree, ".kyber-weave", "kyber-weave.yml"));
        string subdirectory = Path.Combine(worktree, "src");

        CapturedConsoleExecution<int> cycle = ProcessConsoleCapture.Run(() => CreateApp().Run(["review", "cycle", subdirectory]));

        string expected = Path.Combine(worktree, ".agent_scratchpad", "wt-branch", "review", "cycle-1");
        Assert.Contains($"folder: {Path.GetFullPath(expected)}", cycle.Output, StringComparison.Ordinal);
        Assert.Contains("mode: full", cycle.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(CycleFolder(1), cycle.Output, StringComparison.Ordinal);

        (_, string output) = GatesIn(worktree, Path.Combine(expected, "gates.json"));
        Assert.StartsWith("cycle: 1 mode: full", output, StringComparison.Ordinal);
        Assert.Contains($"folder: {Path.GetFullPath(expected)}", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(expected, "cycle.json")));
        Assert.False(File.Exists(Path.Combine(CycleFolder(2), "gates.json")));
    }

    [Fact]
    public void AReviewRunFromTheCanonicalCheckoutNamesTheLiveRunWorktree()
    {
        string worktree = Path.Combine(_temp.Path, "repo.worktrees", "run");
        Git("worktree", "add", "--quiet", "-b", "run-branch", worktree);
        string home = Path.Combine(_temp.Path, "home");
        RunBaseState.Write(home, "run-branch", "master", new RunStartPlace(Path.Combine(_repo, ".git"), worktree, DateTimeOffset.UtcNow));

        string? note = ReviewWorkTree.LiveRunNote(_repo, home);

        Assert.NotNull(note);
        Assert.Contains(RunCheckout.RealPath(worktree), note, StringComparison.Ordinal);
        Assert.Contains("run the review there", note, StringComparison.Ordinal);
        Assert.Null(ReviewWorkTree.LiveRunNote(worktree, home));
    }

    private (int Exit, string Output) GatesIn(string tree, string outPath)
    {
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CreateApp().Run(["review", "gates", tree, "--council", "--out", outPath]));
        return (run.Result, run.Output);
    }

    private string OffBranchCommit()
    {
        Git("checkout", "--quiet", "-b", "side");
        Write("side.txt", "side\n");
        Commit("side");
        string commit = Git("rev-parse", "HEAD").Trim();
        Git("checkout", "--quiet", "feature");
        return commit;
    }

    private (int Exit, string Output) Gates(int cycle, params string[] options)
    {
        string[] arguments = ["review", "gates", _repo, "--council", "--out", GatesPath(cycle), .. options];
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CreateApp().Run(arguments));
        return (run.Result, run.Output);
    }

    private void FinishCycle1(int buildExit = 0, string? branch = null, string? baseCommit = null, string? head = null, bool identity = true)
    {
        GateResult[] gates =
        [
            new("build", true, buildExit, buildExit == 0 ? "ok" : "failed"),
            new("docs", true, 0, "ok"),
            new("always", true, 0, "ok")
        ];
        Write($".agent_scratchpad/{Task}/review/cycle-1/gates.json", ReviewJson.Write(new GateReport(GateReport.CurrentSchema, gates)));
        Write($".agent_scratchpad/{Task}/review/cycle-1/head", Git("rev-parse", "HEAD").Trim());
        if (identity)
        {
            ReviewCycleIdentity current = ReviewWorkTree.IdentityOf(_repo, null)!;
            ReviewCycles.WriteIdentity(CycleFolder(1), new ReviewCycleIdentity(branch ?? current.Branch, baseCommit ?? current.Base, head ?? current.Head));
        }

        Write(
            $".agent_scratchpad/{Task}/review/cycle-1/findings.json",
            ReviewJson.Write(new FindingsReport(
                FindingsReport.CurrentSchema,
                [new ReviewFinding("a/major", "a", ReviewSeverity.Major, 9, "src/a.cs", 1)],
                [],
                0)));
    }

    private string EmptyFindings()
    {
        string path = Path.Combine(_temp.Path, "findings-in.json");
        File.WriteAllText(path, ReviewJson.Write(new FindingsReport(FindingsReport.CurrentSchema, [], [], 0)));
        return path;
    }

    private string CycleFolder(int number) => Path.Combine(_repo, ".agent_scratchpad", Task, "review", $"cycle-{number}");

    private string GatesPath(int cycle) => Path.Combine(CycleFolder(cycle), "gates.json");

    private string ReadGates(int cycle) => File.ReadAllText(GatesPath(cycle));

    private string[] ReportedIds(int cycle) => [.. ReviewJson.ReadGates(ReadGates(cycle)).Gates.Select(gate => gate.Id)];

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
            config.UseStrictParsing();
            config.AddBranch("review", review =>
            {
                review.AddCommand<ReviewGatesCommand>("gates");
                review.AddCommand<ReviewVerdictCommand>("verdict");
                review.AddCommand<ReviewCycleCommand>("cycle");
            });
        });
        return app;
    }
}
