using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review verdict</c> command as a council runs it. Reviewers used to hand-build the findings JSON and
/// seven of eight calls were refused as KW-REVIEW-023. The <c>--seats</c> folder takes each seat's own output
/// instead, and the facts here pin the refusals, the repairs and the diagnostics that replace them.
/// Runs in process over the in-memory file system; no child process, no disk.
/// </summary>
public sealed partial class ReviewVerdictSeatsCommandTests : IDisposable
{
    private const string UnreadableInput = "KW-REVIEW-023";
    private const string LensUnaccounted = "KW-REVIEW-041";
    private const string IncompleteFinding = "KW-REVIEW-001";

    private static readonly string[] DefaultTrailers =
    [
        "Discovery: codegraph 2, docs 0, direct reads 0",
        "REVIEW.md sections checked: [Ground rules, §3]"
    ];

    private static readonly ReviewCycleIdentity Identity = new("task-branch", "base-commit", "head-commit");

    private readonly InMemoryReviewServices _services = new();
    private readonly FakeReviewWorkTreeService _trees;
    private readonly string _repo;
    private readonly string _seats;

    public ReviewVerdictSeatsCommandTests()
    {
        _repo = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _services.Files.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");

        _seats = Path.Combine(_services.Root, "seats");
        _services.Files.CreateDirectory(_seats);

        _trees = new FakeReviewWorkTreeService
        {
            Identities = new Dictionary<string, ReviewCycleIdentity>(StringComparer.Ordinal) { [_repo] = Identity },
            Ancestors = new HashSet<string>(StringComparer.Ordinal) { Identity.Head }
        };
    }

    /// <summary>Releases the in-memory services.</summary>
    public void Dispose()
    {
        _services.Dispose();
    }

    /// <summary>Reproduces cause (a): a findings object with no schema, changedPaths or changedLines.</summary>
    [Fact]
    public void FindingsWithoutSchemaOrChangedPathsIsRefusedAsUnreadable()
    {
        string findings = WriteFile("findings.json", "{\"findings\": []}");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(1, run.Result);
        Assert.Contains(UnreadableInput, run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cause (c) on the legacy path: the seat wrote <c>failure_scenario</c>, which the Web JSON reader does not bind.
    /// The finding must reach the engine with its failure scenario, not be dropped as incomplete.
    /// </summary>
    [Fact]
    public void AFindingsFileWithFailureScenarioKeepsTheFinding()
    {
        string findings = WriteFile("findings.json", """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [
                {
                  "id": "correctness/off-by-one",
                  "lens": "correctness",
                  "severity": "major",
                  "confidence": 9,
                  "file": "src/Foo.cs",
                  "line": 12,
                  "excerpt": "if (i < count)",
                  "claim": "The loop bound excludes the final element.",
                  "evidence": "src/Foo.cs:12",
                  "failure_scenario": "The last element is never processed.",
                  "suggestion": "Use <= or count + 1."
                }
              ],
              "changedPaths": ["src/Foo.cs"],
              "changedLines": 10
            }
            """);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.DoesNotContain(IncompleteFinding, run.Output, StringComparison.Ordinal);
        Assert.Contains("1 finding(s) accepted", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameFindingThroughTheSeatFolderIsAccepted()
    {
        WriteSeat("correctness", FindingSeat());
        string report = Path.Combine(_services.Root, "report.json");

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, .. NotSeatedExcept("correctness"), "--out", report]);

        Assert.Equal(0, run.Result);
        Assert.Equal(["correctness/off-by-one"], ReadFindingIds(report));
    }

    [Fact]
    public void ACatalogueLensWithNoSeatFileOrReasonIsRefusedNamingTheLens()
    {
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict("--seats", _seats);

        Assert.Equal(1, run.Result);
        Assert.Contains(LensUnaccounted, run.Output, StringComparison.Ordinal);
        Assert.Contains("security", run.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Harness.
    // -----------------------------------------------------------------------------------------

    private CommandApp CreateApp() => CommandAppFixture.ReviewVerdict(services =>
    {
        _services.Register(services);
        services.AddSingleton<IReviewWorkTreeService>(_trees);
    }, _services.Ansi);

    private CapturedConsoleExecution<int> Verdict(params string[] options)
    {
        List<string> arguments = ["review", "verdict", _repo];
        arguments.AddRange(options);
        arguments.AddRange(["--format", "json"]);
        return _services.Run(CreateApp(), [.. arguments]);
    }

    /// <summary>A <c>--not-seated</c> option for every catalogue lens except the named ones.</summary>
    private static string[] NotSeatedExcept(params string[] seated)
    {
        return [.. ReviewLensCatalogue.Names
            .Where(name => !seated.Contains(name, StringComparer.Ordinal))
            .SelectMany(name => new[] { "--not-seated", $"{name}=not run in this test" })];
    }

    private static string FindingSeat(
        string lens = "correctness",
        string id = "correctness/off-by-one",
        string severity = "major",
        int line = 12,
        string excerpt = "if (i < count)",
        string[]? trailers = null,
        string file = "src/Foo.cs")
    {
        List<string> lines =
        [
            "```yaml",
            $"- id: {id}",
            $"  lens: {lens}",
            $"  severity: {severity}",
            "  confidence: 9",
            $"  file: {file}",
            $"  line: {line}",
            $"  excerpt: \"{excerpt}\"",
            "  claim: The loop bound excludes the final element.",
            "  evidence: src/Foo.cs",
            "  failure_scenario: The last element is never processed.",
            "  suggestion: Use <= or count + 1.",
            "```"
        ];
        lines.AddRange(trailers ?? DefaultTrailers);
        return string.Join('\n', lines) + "\n";
    }

    private static string NoFindingsSeat(string lens) =>
        $"NO FINDINGS: I checked the {lens} lens and found no defect.\nREVIEW.md sections checked: [Ground rules]\n";

    private void WriteSeat(string lens, string content) => WriteFile($"seats/{lens}.md", content);

    private string WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(_services.Root, relativePath);
        _services.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _services.Files.WriteAllText(path, content);
        return path;
    }

    private string[] ReadFindingIds(string path)
    {
        using JsonDocument document = JsonDocument.Parse(_services.Files.ReadAllText(path));
        return document.RootElement.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("id").GetString() ?? string.Empty)
            .ToArray();
    }
}
