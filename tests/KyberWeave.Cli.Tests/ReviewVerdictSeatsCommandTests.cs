using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review verdict</c> command as a council runs it: the refusals of the input options themselves, and the
/// <c>--prior</c> document's range, spelling and duplicate-name checks. Each refusal names the argument or the file at
/// fault. Runs in process over the in-memory file system; no child process, no disk.
/// </summary>
public sealed partial class ReviewVerdictSeatsCommandTests : IDisposable
{
    private const string UnreadableInput = "KW-REVIEW-023";
    private const string SeatUnparseable = "KW-REVIEW-040";
    private const string LensUnaccounted = "KW-REVIEW-041";
    private const string DropTarget = "KW-REVIEW-042";
    private const string FindingsFlagUsed = "KW-REVIEW-045";
    private const string IncompleteFinding = "KW-REVIEW-001";

    private static readonly string[] DefaultTrailers =
    [
        "Discovery: codegraph 2, docs 0, direct reads 0",
        "REVIEW.md sections checked: [Ground rules, §3]"
    ];

    private static readonly ReviewCycleIdentity Identity = new("task-branch", "aaaaaaaa", "bbbbbbbb");

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

        // A --prior folder counts only when it records the change it reviewed, so the tests' prior belongs to this repository's.
        _services.Cycles.WriteIdentity(PriorFolder, Identity);
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

    /// <summary>Reproduces cause (b): a bare JSON array where the document object is required.</summary>
    [Fact]
    public void ABareFindingsArrayIsRefusedAsUnreadable()
    {
        string findings = WriteFile("findings.json", "[]");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(1, run.Result);
        Assert.Contains(UnreadableInput, run.Output, StringComparison.Ordinal);
    }

    /// <summary>Reproduces cause (c): a finding with no lens, which the engine cannot attribute.</summary>
    [Fact]
    public void AFindingWithNoLensIsRefusedAsUnreadable()
    {
        string findings = WriteFile("findings.json", """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [
                {
                  "id": "correctness/off-by-one",
                  "severity": "major",
                  "confidence": 9,
                  "file": "src/Foo.cs",
                  "line": 12
                }
              ],
              "changedPaths": ["src/Foo.cs"],
              "changedLines": 10
            }
            """);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(1, run.Result);
        Assert.Contains(UnreadableInput, run.Output, StringComparison.Ordinal);
    }

    /// <summary>Reproduces cause (d): a <c>--findings</c> path that does not exist.</summary>
    [Fact]
    public void AMissingFindingsPathIsRefusedAsUnreadable()
    {
        string findings = Path.Combine(_services.Root, "absent.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(1, run.Result);
        Assert.Contains(UnreadableInput, run.Output, StringComparison.Ordinal);
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
    public void UsingTheFindingsOptionEmitsADeprecationWarning()
    {
        string findings = WriteFile("findings.json", """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [],
              "changedPaths": [],
              "changedLines": 0
            }
            """);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Contains(FindingsFlagUsed, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ASeatFileThatCannotBeParsedIsRefusedNamingTheFile()
    {
        WriteSeat("correctness", "```yaml\n- id: [unclosed\n  lens: correctness\n```\n");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        Assert.Contains(SeatUnparseable, run.Output, StringComparison.Ordinal);
        Assert.Contains("correctness.md", run.Output, StringComparison.Ordinal);
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

    /// <summary>
    /// <c>--not-seated</c> is the honest record that a lens did not run. The verdict passes on it and the
    /// reason is printed, so a reader can see that the lens was accounted for and why it has no seat.
    /// </summary>
    [Fact]
    public void ANotSeatedLensPassesAndItsReasonIsReported()
    {
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(LensUnaccounted, run.Output, StringComparison.Ordinal);
        Assert.Contains("not run in this test", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lens that an earlier council covered and that this run did not re-seat is counted as covered when the
    /// previous council's findings document lists it in its coverage.
    /// </summary>
    [Fact]
    public void APriorCouncilThatCoveredALensCountsItAsCovered()
    {
        string prior = Path.Combine(_services.Root, "prior");
        _services.Files.CreateDirectory(prior);
        _services.Files.WriteAllText(Path.Combine(prior, "findings.json"), """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [],
              "changedPaths": [],
              "changedLines": 0,
              "coverage": [ { "lens": "security", "outcome": "noFindings", "reason": null } ]
            }
            """);
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", prior, .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(0, run.Result);
    }

    // -----------------------------------------------------------------------------------------
    // Refusals of the input options themselves: each names the argument at fault.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void SeatsAndFindingsTogetherAreRefusedAsTwoSources()
    {
        WriteSeat("correctness", NoFindingsSeat("correctness"));
        string findings = WriteFile("findings.json", "{}");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--findings", findings]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "two sources");
    }

    [Fact]
    public void AMissingSeatFolderIsRefusedNamingTheFolder()
    {
        string missing = Path.Combine(_services.Root, "no-such-folder");

        CapturedConsoleExecution<int> run = Verdict("--seats", missing);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "Seat folder not found");
    }

    /// <summary>A <c>--seats</c> path that is a file is not a folder of seats: refused naming the path, never a crash.</summary>
    [Fact]
    public void ASeatsPathThatIsAFileIsRefusedNamingThePath()
    {
        string file = WriteFile("not-a-folder.md", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict("--seats", file);

        Assert.Equal(1, run.Result);
        Assert.Contains(file, run.Output, StringComparison.Ordinal);
        AssertDiagnostic(run.Output, UnreadableInput, "Error");
        Assert.Empty(MessagesWithCode(run.Output, SeatUnparseable));
    }

    [Fact]
    public void AMalformedNotSeatedValueIsRefusedNamingTheArgument()
    {
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--not-seated", "security", .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, LensUnaccounted, "Error", "does not name a catalogue lens");
    }

    [Fact]
    public void AMalformedDropValueIsRefusedNamingTheArgument()
    {
        WriteSeat("correctness", FindingSeat());

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--drop", "correctness/off-by-one", .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, DropTarget, "Error", "does not name a finding id and a reason");
    }

    [Fact]
    public void APriorFolderWithoutAFindingsDocumentIsRefused()
    {
        string prior = Path.Combine(_services.Root, "prior");
        _services.Files.CreateDirectory(prior);
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", prior, .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "Input document not found");
    }

    // -----------------------------------------------------------------------------------------
    // --prior is read as JSON, not as a seat, so it needs the range checks the seat parser applies.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("severity", "\"nit\"")]
    [InlineData("severity", "7")]
    [InlineData("confidence", "999")]
    [InlineData("confidence", "-3")]
    [InlineData("confidence", "0")]
    [InlineData("confidence", "1.5")]
    [InlineData("confidence", "\"9\"")]
    [InlineData("confidence", "9.0")]
    [InlineData("confidence", "1e1")]
    [InlineData("confidence", "true")]
    [InlineData("confidence", "null")]
    [InlineData("severity", "null")]
    [InlineData("severity", "true")]
    [InlineData("Severity", "7")]
    [InlineData("CONFIDENCE", "999")]
    public void APriorFindingOutsideTheSeatRangesIsRefusedNamingTheFileAndPath(string field, string rawValue)
    {
        string prior = Path.Combine(_services.Root, "prior");
        _services.Files.CreateDirectory(prior);
        JsonObject finding = new()
        {
            ["id"] = "correctness/off-by-one",
            ["lens"] = "correctness",
            ["severity"] = "major",
            ["confidence"] = 9,
            ["file"] = "src/Foo.cs",
            ["line"] = 12
        };
        finding[field] = JsonNode.Parse(rawValue);
        _services.Files.WriteAllText(
            Path.Combine(prior, "findings.json"),
            "{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[" + finding.ToJsonString() +
            "],\"changedPaths\":[],\"changedLines\":0}");
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", prior, .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        Assert.Contains(MessagesWithCode(run.Output, UnreadableInput), message =>
            message.Contains($"findings[0].{field}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The reader binds names ignoring case, so the prior document's checks do too: an out-of-range finding is refused
    /// under any spelling of the list key and of the field key.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReviewInputDocuments.RangeSpellings), MemberType = typeof(ReviewInputDocuments))]
    public void AnOutOfRangePriorFindingIsRefusedWhateverTheCaseOfTheListAndFieldKeys(
        string listKind, string fieldKind, string field, string rawValue)
    {
        JsonObject finding = PriorFinding();
        finding.Remove(field);
        finding[ReviewInputDocuments.Spell(field, fieldKind)] = JsonNode.Parse(rawValue);
        WritePrior("findings", listKind, new JsonArray(finding));
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        Assert.Contains(MessagesWithCode(run.Output, UnreadableInput), message =>
            message.Contains($"findings[0].{field}", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(ReviewInputDocuments.KnownFindingsLists), MemberType = typeof(ReviewInputDocuments))]
    public void ANullElementInAKnownPriorListIsRefusedWhateverTheCaseOfItsKey(string key, string kind)
    {
        WritePrior(key, kind, new JsonArray("a", null));
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        AssertDiagnostic(run.Output, UnreadableInput, "Error", $"$.{ReviewInputDocuments.Spell(key, kind)}[1]");
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("Pascal")]
    [InlineData("UPPER")]
    public void ANullElementInAnUnknownPriorPropertyIsToleratedWhateverTheCaseOfTheKeys(string kind)
    {
        WritePrior("findings", kind, new JsonArray());
        string path = Path.Combine(PriorFolder, "findings.json");
        JsonObject document = JsonNode.Parse(_services.Files.ReadAllText(path))!.AsObject();
        document[ReviewInputDocuments.Spell("extra", kind)] = new JsonArray(1, null);
        _services.Files.WriteAllText(path, document.ToJsonString());
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
    }

    [Theory]
    [MemberData(nameof(ReviewInputDocuments.DuplicateFindingsKeys), MemberType = typeof(ReviewInputDocuments))]
    public void ATwoSpellingObjectInAPriorDocumentIsRefusedNamingTheFilePathAndBothSpellings(
        string caseName, string path, string first, string second)
    {
        _services.Files.CreateDirectory(PriorFolder);
        _services.Files.WriteAllText(
            Path.Combine(PriorFolder, "findings.json"), ReviewInputDocuments.DuplicateFindingsDocument(caseName));
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        ReviewInputDocuments.AssertRefusedForDuplicate(run, "findings.json", path, first, second);
    }

    [Fact]
    public void AnExactlyRepeatedNameInAPriorDocumentIsRefusedNamingTheFile()
    {
        _services.Files.CreateDirectory(PriorFolder);
        _services.Files.WriteAllText(
            Path.Combine(PriorFolder, "findings.json"),
            "{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[],\"changedPaths\":[],\"changedLines\":0,\"changedLines\":1}");
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
    }

    [Fact]
    public void ATwoSpellingObjectInsideAnUnknownPriorPropertyIsTolerated()
    {
        _services.Files.CreateDirectory(PriorFolder);
        JsonObject document = JsonNode.Parse(ReviewInputDocuments.DuplicateFindingsDocument("coverage-entry"))!.AsObject();
        document["coverage"] = new JsonArray(new JsonObject
        {
            ["lens"] = "security",
            ["outcome"] = "noFindings",
            ["reason"] = null,
            ["meta"] = JsonNode.Parse("{\"x\":1,\"X\":2}")
        });
        document["findings"] = new JsonArray();
        document["extra"] = JsonNode.Parse("{\"a\":1,\"A\":2}");
        _services.Files.WriteAllText(Path.Combine(PriorFolder, "findings.json"), document.ToJsonString());
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
    }

    /// <summary>A prior document written in one spelling, and a severity name in any case, still reads: the rule refuses a repeat, not a spelling.</summary>
    [Theory]
    [InlineData("lower", "minor")]
    [InlineData("Pascal", "MAJOR")]
    [InlineData("UPPER", "Major")]
    public void APriorDocumentWrittenInOneSpellingIsStillRead(string kind, string severity)
    {
        _services.Files.CreateDirectory(PriorFolder);
        _services.Files.WriteAllText(
            Path.Combine(PriorFolder, "findings.json"), ReviewInputDocuments.SpelledFindingsDocument(kind, severity));
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        Assert.NotEmpty(MessagesWithCode(run.Output, "KW-REVIEW-024"));
    }

    // -----------------------------------------------------------------------------------------
    // Empty option values and empty prior documents: refused, naming the option or the file.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("--seats")]
    [InlineData("--prior")]
    [InlineData("--out")]
    [InlineData("--gates")]
    public void AnEmptyOptionValueIsRefusedNamingTheOption(string option)
    {
        WriteSeat("correctness", NoFindingsSeat("correctness"));
        List<string> arguments = option == "--seats"
            ? ["--seats", ""]
            : ["--seats", _seats, .. NotSeatedExcept("correctness"), option, ""];

        CapturedConsoleExecution<int> run = Verdict([.. arguments]);

        Assert.Equal(1, run.Result);
        Assert.Contains(option, run.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void AnEmptyPriorFindingsDocumentIsRefusedNamingTheFile(string content)
    {
        string prior = Path.Combine(_services.Root, "prior");
        _services.Files.CreateDirectory(prior);
        _services.Files.WriteAllText(Path.Combine(prior, "findings.json"), content);
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", prior, .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
    }

    [Fact]
    public void ANotSeatedNameThatIsNotALensIsRefused()
    {
        WriteSeat("correctness", FindingSeat());

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--not-seated", "bogus=some reason", .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, LensUnaccounted, "Error", "does not name a catalogue lens");
    }

    [Fact]
    public void ANotSeatedWithABlankReasonIsRefused()
    {
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--not-seated", "security=", .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, LensUnaccounted, "Error", "does not name a catalogue lens and a reason");
    }

    [Fact]
    public void ADropWithABlankReasonIsRefused()
    {
        WriteSeat("correctness", FindingSeat());

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--drop", "correctness/off-by-one=", .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, DropTarget, "Error", "does not name a finding id and a reason");
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
        List<string> arguments = ["review", "verdict", _repo, .. options, "--format", "json"];
        return _services.Run(CreateApp(), [.. arguments]);
    }

    /// <summary>A <c>--not-seated</c> option for every catalogue lens except the named ones.</summary>
    private static string[] NotSeatedExcept(params string[] seated)
    {
        return [.. ReviewLensCatalogue.Names
            .Where(name => !seated.Contains(name, StringComparer.Ordinal))
            .SelectMany(name => new[] { "--not-seated", $"{name}=not run in this test" })];
    }

    private void WriteSeat(string lens, string content) => WriteFile($"seats/{lens}.md", content);

    private void WriteSource(string content) => WriteFile("repo/src/Foo.cs", content);

    private string PriorFolder => Path.Combine(_services.Root, "prior");

    private static JsonObject PriorFinding() => new()
    {
        ["id"] = "correctness/off-by-one",
        ["lens"] = "correctness",
        ["severity"] = "major",
        ["confidence"] = 9,
        ["file"] = "src/Foo.cs",
        ["line"] = 12
    };

    /// <summary>Writes the prior folder's findings document with <paramref name="key"/> holding <paramref name="value"/> under the spelling <paramref name="kind"/> names.</summary>
    private void WritePrior(string key, string kind, JsonNode value)
    {
        _services.Files.CreateDirectory(PriorFolder);
        JsonObject document = new()
        {
            ["schema"] = "kyber-weave.review-findings/v1",
            ["findings"] = new JsonArray(),
            ["changedPaths"] = new JsonArray(),
            ["changedLines"] = 0
        };
        document.Remove(key);
        document[ReviewInputDocuments.Spell(key, kind)] = value;
        _services.Files.WriteAllText(Path.Combine(PriorFolder, "findings.json"), document.ToJsonString());
    }

    private static string FindingSeat(
        string lens = "correctness",
        string id = "correctness/off-by-one",
        string severity = "major",
        int line = 12,
        string excerpt = "if (i < count)",
        string[]? trailers = null,
        string file = "src/Foo.cs",
        string? kind = null,
        string? basis = null)
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
            "  suggestion: Use <= or count + 1."
        ];
        if (kind is not null)
            lines.Add($"  kind: {kind}");
        if (basis is not null)
            lines.Add($"  basis: \"{basis}\"");
        lines.Add("```");
        lines.AddRange(trailers ?? DefaultTrailers);
        return string.Join('\n', lines) + "\n";
    }

    private static string NoFindingsSeat(string lens) =>
        $"NO FINDINGS: I checked the {lens} lens and found no defect.\nREVIEW.md sections checked: [Ground rules]\n";

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
