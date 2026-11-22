using KyberWeave.Cli.Commands.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     A <c>--fix</c> task-gate run whose fix commands changed no task file is the verification run: it prints the
///     completion digest and writes the passing report a plain run writes. One whose fixers did change a task file
///     still never counts, and says to run again without <c>--fix</c>.
/// </summary>
public sealed class ReviewGatesFixRunTests : IDisposable
{
    private const string TaskFile = "src/Widget.cs";

    private readonly InMemoryReviewServices _services = new();
    private readonly string _repo;

    public ReviewGatesFixRunTests()
    {
        _repo = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _services.Files.CreateDirectory(Path.Combine(_repo, "src"));
        _services.Files.WriteAllText(Path.Combine(_repo, TaskFile), "class Widget { }\n");
        _services.Files.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            """
            review:
              task-gates:
                csharp-dev:
                  - id: formatted
                    include: ["**/*.cs"]
                    fix: [format]
                    run: [verify]
            """);
        _services.ScriptProgram("verify", TimeSpan.Zero);
    }

    /// <summary>Releases the services.</summary>
    public void Dispose()
    {
        _services.Dispose();
    }

    [Fact]
    public void AFixRunThatChangedNothingPrintsTheDigestAndWritesThePassingReport()
    {
        _services.Processes.Script("format", _ => new ProcessRunResult(0, string.Empty, TimedOut: false));

        CapturedConsoleExecution<int> run = Gates("--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile, "--fix");

        Assert.Equal(0, run.Result);
        Assert.Contains("STATUS: PASS", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("without --fix", run.Output, StringComparison.Ordinal);
        GateReport report = ReviewJson.ReadGates(
            _services.Files.ReadAllText(Path.Combine(_repo, TaskGateNextStep.PassingReportPath("t1"))));
        Assert.True(Assert.Single(report.Gates).Passed);
    }

    [Fact]
    public void AFixRunThatChangedNothingStillWritesThePassingReportWhenOutNamesAnotherFile()
    {
        _services.Processes.Script("format", _ => new ProcessRunResult(0, string.Empty, TimedOut: false));
        string outPath = Path.Combine(_repo, "reports", "custom.json");

        CapturedConsoleExecution<int> run = Gates(
            "--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile, "--fix", "--out", outPath);

        Assert.Equal(0, run.Result);
        Assert.Contains("STATUS: PASS", run.Output, StringComparison.Ordinal);
        Assert.True(_services.Files.FileExists(outPath));
        GateReport report = ReviewJson.ReadGates(
            _services.Files.ReadAllText(Path.Combine(_repo, TaskGateNextStep.PassingReportPath("t1"))));
        Assert.True(Assert.Single(report.Gates).Passed);
    }

    [Fact]
    public void AFixRunThatChangedATaskFileStillSaysToVerifyWithoutFix()
    {
        _services.Processes.Script("format", _ =>
        {
            _services.Files.WriteAllText(Path.Combine(_repo, TaskFile), "class Widget { int Id; }\n");
            return new ProcessRunResult(0, string.Empty, TimedOut: false);
        });

        CapturedConsoleExecution<int> run = Gates("--agent", "csharp-dev", "--task", "t1", "--paths", TaskFile, "--fix");

        Assert.Equal(0, run.Result);
        Assert.Contains("without --fix", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("STATUS: PASS", run.Output, StringComparison.Ordinal);
        Assert.False(_services.Files.FileExists(Path.Combine(_repo, TaskGateNextStep.PassingReportPath("t1"))));
    }

    /// <summary>Runs <c>review gates</c> on the repository with the default human output, so the next-step lines print.</summary>
    private CapturedConsoleExecution<int> Gates(params string[] options) =>
        _services.Run(_services.ReviewGatesApp(), ["review", "gates", _repo, .. options]);
}
