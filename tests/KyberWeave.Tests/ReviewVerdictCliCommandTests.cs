using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies the CLI command registration, help descriptions, and execution for <c>review verdict</c>.
/// Runs 100% in-process with zero child process spawns.
/// </summary>
public sealed class ReviewVerdictCliCommandTests
{
    /// <summary>
    /// The <c>review verdict</c> command description registered in <c>Program.cs</c> must not
    /// cite the retired <c>needs-human</c> verdict option, and must state the binary verdict contract.
    /// </summary>
    [Fact]
    public void ProgramRegistersReviewVerdictWithoutNeedsHumanInDescription()
    {
        string program = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Cli",
            "Program.cs"));

        int verdictIndex = program.IndexOf("AddCommand<ReviewVerdictCommand>(\"verdict\")", StringComparison.Ordinal);
        Assert.True(verdictIndex >= 0, "Program.cs must register the review verdict command.");

        int nextCommandIndex = program.IndexOf(".AddCommand", verdictIndex + 1, StringComparison.Ordinal);
        string verdictRegistration = nextCommandIndex >= 0
            ? program[verdictIndex..nextCommandIndex]
            : program[verdictIndex..];

        Assert.DoesNotContain("needs-human", verdictRegistration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Compute approve / request-changes from findings and gates.", verdictRegistration, StringComparison.Ordinal);
    }

    /// <summary>
    /// Running <c>review verdict --help</c> against an in-process <see cref="CommandApp"/> must not include
    /// the retired <c>needs-human</c> verdict option in the rendered command description.
    /// </summary>
    [Fact]
    public void ReviewVerdictHelpDescriptionDoesNotContainNeedsHuman()
    {
        CommandApp app = CreateApp();
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => app.Run(["review", "verdict", "--help"]));

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
        CommandApp app = CreateApp();
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => app.Run(["review", "--help"]));

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
        using TempDirectory temp = new();
        string repoDir = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(repoDir, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");

        string findingsPath = Path.Combine(temp.Path, "findings.json");
        File.WriteAllText(findingsPath, """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [],
              "changedPaths": ["src/Foo.cs"],
              "changedLines": 10
            }
            """);

        string gatesPath = Path.Combine(temp.Path, "gates.json");
        GateReport gates = new(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "passed")]);
        File.WriteAllText(gatesPath, ReviewJson.Write(gates));

        ReviewVerdictSettings settings = new()
        {
            Path = repoDir,
            Findings = findingsPath,
            Gates = gatesPath,
            Format = "json"
        };

        ReviewVerdictCommand command = new();
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => command.Execute(null!, settings, CancellationToken.None));

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
        using TempDirectory temp = new();
        string repoDir = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(repoDir, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");

        string findingsPath = Path.Combine(temp.Path, "findings.json");
        File.WriteAllText(findingsPath, """
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

        string gatesPath = Path.Combine(temp.Path, "gates.json");
        GateReport gates = new(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "passed")]);
        File.WriteAllText(gatesPath, ReviewJson.Write(gates));

        ReviewVerdictSettings settings = new()
        {
            Path = repoDir,
            Findings = findingsPath,
            Gates = gatesPath,
            Format = "json"
        };

        ReviewVerdictCommand command = new();
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => command.Execute(null!, settings, CancellationToken.None));

        Assert.Equal(1, run.Result);
        Assert.Contains("REQUEST CHANGES", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("needs-human", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static CommandApp CreateApp()
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.AddBranch("review", review =>
            {
                review.SetDescription("Run the declared review gates and compute the review verdict.");
                review.AddCommand<ReviewVerdictCommand>("verdict")
                    .WithDescription("Compute approve / request-changes from findings and gates.")
                    .WithExample("review", "verdict", ".", "--findings", "findings.json", "--gates", "gates.json");
            });
        });
        return app;
    }
}
