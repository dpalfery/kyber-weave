using KyberWeave.Cli;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Review;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies the CLI help descriptions and execution for <c>review verdict</c>, in-process over in-memory fakes.
/// </summary>
public sealed class ReviewVerdictCliCommandTests : IDisposable
{
    private readonly InMemoryReviewServices _services = new();

    public void Dispose() => _services.Dispose();

    /// <summary>
    /// An undefined verdict must produce a readable summary so the command can finish and return the
    /// defensive exit code instead of throwing out of <c>Execute</c>.
    /// </summary>
    [Fact]
    public void DescribeForSummary_WhenVerdictIsUndefined_ReturnsInvalidLabel()
    {
        Assert.Equal(
            "INVALID VERDICT (99)",
            ReviewVerdictCommand.DescribeForSummary((ReviewVerdict)99));
    }

    /// <summary>
    /// Running <c>review verdict --help</c> against an in-process <see cref="CommandApp"/> must not include
    /// the retired <c>needs-human</c> verdict option in the rendered command description.
    /// </summary>
    [Fact]
    public void ReviewVerdictHelpDescriptionDoesNotContainNeedsHuman()
    {
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CreateApp().Run(["review", "verdict", "--help"]));

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain("needs-human", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approve / request-changes", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Running <c>review --help</c> against an in-process <see cref="CommandApp"/> must not include
    /// <c>needs-human</c> in the summary for the <c>verdict</c> subcommand.
    /// </summary>
    [Fact]
    public void ReviewBranchHelpDescriptionDoesNotContainNeedsHuman()
    {
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CreateApp().Run(["review", "--help"]));

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain("needs-human", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verdict", run.Output, StringComparison.Ordinal);
        Assert.Contains("approve / request-changes", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Executing <see cref="ReviewVerdictCommand"/> in-process with clean findings and passing gates
    /// returns exit code 0 (APPROVE) and contains no references to <c>needs-human</c>.
    /// </summary>
    [Fact]
    public void ReviewVerdictCommandApprovesCleanFindingsInProcess()
    {
        string repoDir = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(repoDir, ".kyber-weave"));
        _services.Files.WriteAllText(
            Path.Combine(repoDir, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");

        string findingsPath = Path.Combine(_services.Root, "findings.json");
        _services.Files.WriteAllText(findingsPath, """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [],
              "changedPaths": ["src/Foo.cs"],
              "changedLines": 10
            }
            """);

        string gatesPath = Path.Combine(_services.Root, "gates.json");
        GateReport gates = new(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "passed")]);
        _services.Files.WriteAllText(gatesPath, ReviewJson.Write(gates));

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CreateApp().Run(
                ["review", "verdict", repoDir, "--findings", findingsPath, "--gates", gatesPath, "--format", "json"]));

        Assert.Equal(0, run.Result);
        Assert.Contains("APPROVE", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("needs-human", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Executing <see cref="ReviewVerdictCommand"/> in-process with a critical finding returns exit code 1
    /// (REQUEST CHANGES) and contains no references to <c>needs-human</c>.
    /// </summary>
    [Fact]
    public void ReviewVerdictCommandRequestsChangesOnCriticalFindingInProcess()
    {
        string repoDir = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(repoDir, ".kyber-weave"));
        _services.Files.WriteAllText(
            Path.Combine(repoDir, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");

        string findingsPath = Path.Combine(_services.Root, "findings.json");
        _services.Files.WriteAllText(findingsPath, """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [
                {
                  "id": "correctness/null-dereference",
                  "lens": "correctness",
                  "severity": "critical",
                  "confidence": 9,
                  "file": "src/Foo.cs",
                  "line": 12,
                  "excerpt": "x.ToString()",
                  "claim": "Potential null dereference.",
                  "evidence": "src/Foo.cs:12",
                  "failureScenario": "Calling method on null throws NullReferenceException.",
                  "suggestion": "Check for null first."
                }
              ],
              "changedPaths": ["src/Foo.cs"],
              "changedLines": 10
            }
            """);

        string gatesPath = Path.Combine(_services.Root, "gates.json");
        GateReport gates = new(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "passed")]);
        _services.Files.WriteAllText(gatesPath, ReviewJson.Write(gates));

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CreateApp().Run(
                ["review", "verdict", repoDir, "--findings", findingsPath, "--gates", gatesPath, "--format", "json"]));

        Assert.Equal(1, run.Result);
        Assert.Contains("REQUEST CHANGES", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("needs-human", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    private CommandApp CreateApp() => CliApplication.Create(_services.Register);
}
