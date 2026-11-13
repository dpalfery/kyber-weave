using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
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
    public void AFirstCycleRunsEveryGateAndPrintsNoCycleHeader()
    {
        (int exit, string output) = Gates(1, "--format", "lines");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("mode:", output, StringComparison.Ordinal);
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

        Assert.DoesNotContain("mode:", output, StringComparison.Ordinal);
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

        Assert.DoesNotContain("mode:", output, StringComparison.Ordinal);
    }

    private (int Exit, string Output) Gates(int cycle, params string[] options)
    {
        string[] arguments = ["review", "gates", _repo, "--council", "--out", GatesPath(cycle), .. options];
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CreateApp().Run(arguments));
        return (run.Result, run.Output);
    }

    private void FinishCycle1(int buildExit = 0)
    {
        GateResult[] gates =
        [
            new("build", true, buildExit, buildExit == 0 ? "ok" : "failed"),
            new("docs", true, 0, "ok"),
            new("always", true, 0, "ok")
        ];
        Write($".agent_scratchpad/{Task}/review/cycle-1/gates.json", ReviewJson.Write(new GateReport(GateReport.CurrentSchema, gates)));
        Write($".agent_scratchpad/{Task}/review/cycle-1/head", Git("rev-parse", "HEAD").Trim());
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
            });
        });
        return app;
    }
}
