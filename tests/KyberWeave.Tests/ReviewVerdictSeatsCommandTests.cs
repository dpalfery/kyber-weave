using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;
using KyberWeave.Tests.Fakes;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review verdict</c> command as a council runs it. Reviewers used to hand-build the findings JSON and
/// seven of eight calls were refused as KW-REVIEW-023. The <c>--seats</c> folder takes each seat's own output
/// instead, and the facts here pin the refusals, the repairs and the diagnostics that replace them.
/// Runs in process against a temporary repository, with no child processes.
/// </summary>
public sealed class ReviewVerdictSeatsCommandTests : IDisposable
{
    private const string UnreadableInput = "KW-REVIEW-023";
    private const string SeatUnparseable = "KW-REVIEW-040";
    private const string LensUnaccounted = "KW-REVIEW-041";
    private const string DropTarget = "KW-REVIEW-042";
    private const string LineMoved = "KW-REVIEW-043";
    private const string DiscoveryMissing = "KW-REVIEW-044";
    private const string FindingsFlagUsed = "KW-REVIEW-045";
    private const string AcceptedFindings = "KW-REVIEW-046";
    private const string IncompleteFinding = "KW-REVIEW-001";
    private const string InputSitesMissing = "KW-REVIEW-047";

    private static readonly string[] DefaultTrailers =
    [
        "Discovery: codegraph 2, docs 0, direct reads 0",
        "REVIEW.md sections checked: [Ground rules, §3]"
    ];

    private readonly TempDirectory _temp = new();
    private readonly string _repo;
    private readonly string _seats;

    public ReviewVerdictSeatsCommandTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        TestGitRepositoryTemplate.CreateReviewGatesCouncilRepository(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");

        _seats = Path.Combine(_temp.Path, "seats");
        Directory.CreateDirectory(_seats);

        // A --prior folder counts only when it records the change it reviewed, so the tests' prior belongs to this repository's.
        ReviewCycles.WriteIdentity(PriorFolder, ReviewWorkTree.IdentityOf(_repo, null)!);
    }

    /// <summary>Deletes the temporary repository and seat folders.</summary>
    public void Dispose()
    {
        _temp.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // The four reproductions of today's KW-REVIEW-023 failures. These pass on the current code.
    // -----------------------------------------------------------------------------------------

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
        string findings = Path.Combine(_temp.Path, "absent.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(1, run.Result);
        Assert.Contains(UnreadableInput, run.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // The seat folder: one <lens>.md per seat, parsed by SeatOutputParser.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheSameFindingThroughTheSeatFolderIsAccepted()
    {
        WriteSeat("correctness", FindingSeat());
        string report = Path.Combine(_temp.Path, "report.json");

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
        string prior = Path.Combine(_temp.Path, "prior");
        Directory.CreateDirectory(prior);
        File.WriteAllText(Path.Combine(prior, "findings.json"), """
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
    // --drop removes a finding only with a stated reason; no option changes a severity.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DropRemovesTheFindingAndStatesItsReason()
    {
        WriteSeat("correctness", FindingSeat());
        string report = Path.Combine(_temp.Path, "report.json");

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--drop", "correctness/off-by-one=duplicate of another finding", "--out", report,
                .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(DropTarget, run.Output, StringComparison.Ordinal);
        Assert.Contains("duplicate of another finding", run.Output, StringComparison.Ordinal);
        Assert.Empty(ReadFindingIds(report));
    }

    [Fact]
    public void DropNamingNoFindingIsRefused()
    {
        WriteSeat("correctness", FindingSeat());

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--drop", "correctness/missing=no such finding", .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        Assert.Contains(DropTarget, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOptionChangesTheSeverityASeatGave()
    {
        WriteSeat("correctness", FindingSeat(severity: "major"));
        string report = Path.Combine(_temp.Path, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
        JsonElement finding = document.RootElement.GetProperty("findings")[0];
        Assert.Equal("major", finding.GetProperty("severity").GetString());
    }

    // -----------------------------------------------------------------------------------------
    // A line the seat gave that does not exist in the file is repaired by its excerpt, or dropped as a warning.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ALineBeyondTheFileEndMovesToTheUniqueLineHoldingTheExcerpt()
    {
        WriteSource("public int Total(int[] items)\n{\n    int total = 0;\n    return total;\n}\n");
        WriteSeat("correctness", FindingSeat(line: 99, excerpt: "    return total;"));
        string report = Path.Combine(_temp.Path, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(LineMoved, run.Output, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(4, document.RootElement.GetProperty("findings")[0].GetProperty("line").GetInt32());
    }

    [Fact]
    public void ALineBeyondTheFileEndWithNoUniqueMatchIsDroppedAsAWarning()
    {
        WriteSource("int A()\n{\n    return total;\n}\nint B()\n{\n    return total;\n}\n");
        WriteSeat("correctness", FindingSeat(line: 99, excerpt: "    return total;"));
        string report = Path.Combine(_temp.Path, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        AssertDiagnostic(run.Output, LineMoved, "Warning");
        Assert.Empty(ReadFindingIds(report));
    }

    [Fact]
    public void ALineInsideTheFileIsLeftWhereItIs()
    {
        WriteSource("public int Total(int[] items)\n{\n    int total = 0;\n    return total;\n}\n");
        WriteSeat("correctness", FindingSeat(line: 3, excerpt: "    return total;"));
        string report = Path.Combine(_temp.Path, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain(LineMoved, run.Output, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(3, document.RootElement.GetProperty("findings")[0].GetProperty("line").GetInt32());
    }

    // -----------------------------------------------------------------------------------------
    // Discovery evidence: a seat that read files directly without saying why gets a warning, never a refusal.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("Discovery: codegraph 0, docs 0, direct reads 3")]
    public void AFindingsSeatWithoutIndexEvidenceWarnsAndKeepsTheVerdict(string discoveryLine)
    {
        string[] trailers = discoveryLine.Length == 0
            ? ["REVIEW.md sections checked: [Ground rules, §3]"]
            : [discoveryLine, "REVIEW.md sections checked: [Ground rules, §3]"];
        WriteSeat("correctness", FindingSeat(trailers: trailers));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, DiscoveryMissing, "Warning");
    }

    [Fact]
    public void AFindingsSeatWithIndexEvidenceDoesNotWarn()
    {
        WriteSeat("correctness", FindingSeat());

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain(DiscoveryMissing, run.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Accepted findings are listed, unless a blocking gate already failed.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheAcceptedCriticalAndMajorIdsAreListedWhenNoBlockingGateFailed()
    {
        WriteSeat("correctness", FindingSeat(severity: "critical"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Contains(AcceptedFindings, run.Output, StringComparison.Ordinal);
        Assert.Contains("correctness/off-by-one", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAcceptedListIsAbsentWhenABlockingGateFailed()
    {
        WriteSeat("correctness", FindingSeat(severity: "critical"));
        string gates = WriteFile("gates.json", ReviewJson.Write(new GateReport(
            GateReport.CurrentSchema,
            [new GateResult("build", true, 1, "2 errors")])));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--gates", gates, .. NotSeatedExcept("correctness")]);

        Assert.DoesNotContain(AcceptedFindings, run.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // The findings document the verdict writes.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void OutWritesAFindingsReportWithTheSchemaAndACoverageEntryPerLens()
    {
        WriteSeat("correctness", NoFindingsSeat("correctness"));
        string report = Path.Combine(_temp.Path, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
        Assert.Equal(FindingsReport.CurrentSchema, document.RootElement.GetProperty("schema").GetString());
        string[] lenses = document.RootElement.GetProperty("coverage").EnumerateArray()
            .Select(entry => entry.GetProperty("lens").GetString() ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ReviewLensCatalogue.Names.Order(StringComparer.Ordinal), lenses);
    }

    /// <summary>
    /// <c>reviewer.md</c> is the seat that reviews the findings themselves. It is not a catalogue lens, so it
    /// is accepted under its own name rather than refused as an unknown seat.
    /// </summary>
    [Fact]
    public void AReviewerSeatFileIsAcceptedWithTheReviewerLens()
    {
        WriteSeat("reviewer", FindingSeat(lens: "reviewer", id: "reviewer/unclear-contract"));
        string report = Path.Combine(_temp.Path, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept()]);

        Assert.Equal(0, run.Result);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
        JsonElement finding = document.RootElement.GetProperty("findings")[0];
        Assert.Equal("reviewer", finding.GetProperty("lens").GetString());
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
        string missing = Path.Combine(_temp.Path, "no-such-folder");

        CapturedConsoleExecution<int> run = Verdict("--seats", missing);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "Seat folder not found");
    }

    /// <summary>
    /// A seats folder the process cannot list is a seat folder that cannot be read: KW-REVIEW-040 naming the folder, not
    /// an unhandled UnauthorizedAccessException out of the command.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ASeatFolderThatCannotBeListedIsRefusedUnderKw040NamingTheFolder()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            // Windows and root ignore the directory mode, so the denied listing cannot be produced here.
            Assert.Skip("Directory listing permission is not enforced for this user on this platform.");
        }

        string locked = Path.Combine(_temp.Path, "locked-seats");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            CapturedConsoleExecution<int> run = Verdict("--seats", locked);

            Assert.Equal(1, run.Result);
            AssertDiagnostic(run.Output, SeatUnparseable, "Error", locked);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
        string prior = Path.Combine(_temp.Path, "prior");
        Directory.CreateDirectory(prior);
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
        File.WriteAllText(
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
    [MemberData(nameof(ReviewVerdictInputTests.RangeSpellings), MemberType = typeof(ReviewVerdictInputTests))]
    public void AnOutOfRangePriorFindingIsRefusedWhateverTheCaseOfTheListAndFieldKeys(
        string listKind, string fieldKind, string field, string rawValue)
    {
        JsonObject finding = PriorFinding();
        finding.Remove(field);
        finding[ReviewVerdictInputTests.Spell(field, fieldKind)] = JsonNode.Parse(rawValue);
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
    [MemberData(nameof(ReviewVerdictInputTests.KnownFindingsLists), MemberType = typeof(ReviewVerdictInputTests))]
    public void ANullElementInAKnownPriorListIsRefusedWhateverTheCaseOfItsKey(string key, string kind)
    {
        WritePrior(key, kind, new JsonArray("a", null));
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        AssertDiagnostic(run.Output, UnreadableInput, "Error", $"$.{ReviewVerdictInputTests.Spell(key, kind)}[1]");
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("Pascal")]
    [InlineData("UPPER")]
    public void ANullElementInAnUnknownPriorPropertyIsToleratedWhateverTheCaseOfTheKeys(string kind)
    {
        WritePrior("findings", kind, new JsonArray());
        string path = Path.Combine(PriorFolder, "findings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document[ReviewVerdictInputTests.Spell("extra", kind)] = new JsonArray(1, null);
        File.WriteAllText(path, document.ToJsonString());
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
    }

    [Theory]
    [MemberData(nameof(ReviewVerdictInputTests.DuplicateFindingsKeys), MemberType = typeof(ReviewVerdictInputTests))]
    public void ATwoSpellingObjectInAPriorDocumentIsRefusedNamingTheFilePathAndBothSpellings(
        string caseName, string path, string first, string second)
    {
        Directory.CreateDirectory(PriorFolder);
        File.WriteAllText(Path.Combine(PriorFolder, "findings.json"), ReviewVerdictInputTests.DuplicateFindingsDocument(caseName));
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        ReviewVerdictInputTests.AssertRefusedForDuplicate(run, "findings.json", path, first, second);
    }

    [Fact]
    public void AnExactlyRepeatedNameInAPriorDocumentIsRefusedNamingTheFile()
    {
        Directory.CreateDirectory(PriorFolder);
        File.WriteAllText(
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
        Directory.CreateDirectory(PriorFolder);
        JsonObject document = JsonNode.Parse(ReviewVerdictInputTests.DuplicateFindingsDocument("coverage-entry"))!.AsObject();
        document["coverage"] = new JsonArray(new JsonObject
        {
            ["lens"] = "security",
            ["outcome"] = "noFindings",
            ["reason"] = null,
            ["meta"] = JsonNode.Parse("{\"x\":1,\"X\":2}")
        });
        document["findings"] = new JsonArray();
        document["extra"] = JsonNode.Parse("{\"a\":1,\"A\":2}");
        File.WriteAllText(Path.Combine(PriorFolder, "findings.json"), document.ToJsonString());
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
        Directory.CreateDirectory(PriorFolder);
        File.WriteAllText(Path.Combine(PriorFolder, "findings.json"), ReviewVerdictInputTests.SpelledFindingsDocument(kind, severity));
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", PriorFolder, .. NotSeatedExcept("correctness")]);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        Assert.NotEmpty(MessagesWithCode(run.Output, "KW-REVIEW-024"));
    }

    private string PriorFolder => Path.Combine(_temp.Path, "prior");

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
        Directory.CreateDirectory(PriorFolder);
        JsonObject document = new()
        {
            ["schema"] = "kyber-weave.review-findings/v1",
            ["findings"] = new JsonArray(),
            ["changedPaths"] = new JsonArray(),
            ["changedLines"] = 0
        };
        document.Remove(key);
        document[ReviewVerdictInputTests.Spell(key, kind)] = value;
        File.WriteAllText(Path.Combine(PriorFolder, "findings.json"), document.ToJsonString());
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
        string prior = Path.Combine(_temp.Path, "prior");
        Directory.CreateDirectory(prior);
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", prior, .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "Input document not found");
    }

    // -----------------------------------------------------------------------------------------
    // A seat that declares an outcome and also holds findings is refused, never counted clean.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void ASeatThatDeclaresAnOutcomeAndAlsoHoldsFindingsIsRefusedNamingTheFile()
    {
        WriteSeat("correctness", "NO FINDINGS: I found nothing.\n" + FindingSeat());

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, SeatUnparseable, "Error", "correctness.md");
    }

    // -----------------------------------------------------------------------------------------
    // Closing lines the verdict checks: an unverifiable cited line, and an unknown fallback mode.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A finding cites a file the repository does not have, so its line cannot be verified. The verdict says so
    /// under KW-REVIEW-043 rather than leaving the citation silently unchecked.
    /// </summary>
    [Fact]
    public void ACitedLineInAFileThatIsNotThereGetsAKw043Diagnostic()
    {
        WriteSeat("correctness", FindingSeat(line: 12));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnosticCode(run.Output, LineMoved);
    }

    [Fact]
    public void ACitedLineOutsideTheRepositoryGetsAKw043Diagnostic()
    {
        WriteSeat("correctness", FindingSeat(file: "../outside.cs", line: 12));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnosticCode(run.Output, LineMoved);
    }

    /// <summary>
    /// The seat named an index fallback mode the seat contract does not define. The verdict does not change, but the
    /// reader is told the closing line is one the reviewer cannot rely on.
    /// </summary>
    [Fact]
    public void AnIndexFallbackModeThatIsNotKnownWarnsUnderKw044()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Index fallback: not-used",
            "Discovery: codegraph 2, docs 0, direct reads 0",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, DiscoveryMissing, "Warning", "not-used");
    }

    [Fact]
    public void AKnownIndexFallbackModeIsNotWarnedAbout()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Index fallback: projectPath: not-indexed",
            "Discovery: codegraph 0, docs 0, direct reads 6",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain(DiscoveryMissing, run.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // The correctness seat's Input sites line. A seat that reported findings or NO FINDINGS and
    // wrote no readable count gets KW-REVIEW-047, a coverage warning that never changes the verdict.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("findings")]
    [InlineData("no findings")]
    public void ACorrectnessSeatWithoutInputSitesWarnsUnderKw047(string shape)
    {
        WriteSeat("correctness", shape == "findings" ? FindingSeat() : NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, InputSitesMissing, "Warning", "correctness");
    }

    [Fact]
    public void ACorrectnessSeatWithInputSitesDoesNotWarnUnderKw047()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Input sites: 4 traced, 2 files with none",
            "Discovery: codegraph 2, docs 0, direct reads 0",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, InputSitesMissing));
    }

    [Fact]
    public void ASkippedCorrectnessSeatIsExemptFromKw047()
    {
        WriteSeat("correctness", "SKIPPED: the lens could not run in this session.\n");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, InputSitesMissing));
    }

    [Fact]
    public void AnotherLensWithoutInputSitesDoesNotWarnUnderKw047()
    {
        WriteSeat("security", FindingSeat(lens: "security", id: "security/unchecked-redirect"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("security")]);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, InputSitesMissing));
    }

    /// <summary>The warning is a coverage note: a critical finding still requests changes, and the warning is there beside it.</summary>
    [Fact]
    public void Kw047NeverChangesTheVerdictOrTheExitCode()
    {
        WriteSeat("correctness", FindingSeat(severity: "critical"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, InputSitesMissing, "Warning", "correctness");
    }

    // -----------------------------------------------------------------------------------------
    // An unverified citation: the message names the repository-relative path only, and the
    // diagnostic is per file, not per finding.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AnUnverifiedMissingFileMessageNamesOnlyTheRepositoryRelativePath()
    {
        WriteSeat("correctness", FindingSeat(file: "src/Missing.cs", line: 12));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        List<string> messages = MessagesWithCode(run.Output, LineMoved);
        string message = Assert.Single(messages);
        Assert.Contains("src/Missing.cs", message, StringComparison.Ordinal);
        Assert.DoesNotContain(_temp.Path, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two findings citing one file that cannot be checked get one KW-REVIEW-043 info, naming the file once, not one
    /// per finding. The three kinds of citation each take the same path: a missing file, a path that leaves the
    /// repository, and a rooted path.
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("dotdot")]
    [InlineData("rooted")]
    public void AnUnverifiedFileIsReportedOncePerFileNotPerFinding(string kind)
    {
        string file = kind switch
        {
            "missing" => "src/Missing.cs",
            "dotdot" => "../outside.cs",
            _ => Path.Combine(_temp.Path, "outside.cs")
        };
        WriteSeat("correctness", TwoFindingsCiting(file));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Single(MessagesWithCode(run.Output, LineMoved));
        AssertDiagnostic(run.Output, LineMoved, "Info");
    }

    // -----------------------------------------------------------------------------------------
    // --not-seated and --drop operands that contain '=' but have no usable reason.
    // -----------------------------------------------------------------------------------------

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

    /// <summary>Two findings in one seat, both citing <paramref name="file"/> and both cited past the end of it.</summary>
    private static string TwoFindingsCiting(string file)
    {
        List<string> lines = ["```yaml"];
        foreach (string id in new[] { "correctness/a", "correctness/b" })
        {
            lines.Add($"- id: {id}");
            lines.Add("  lens: correctness");
            lines.Add("  severity: major");
            lines.Add("  confidence: 9");
            lines.Add($"  file: {file}");
            lines.Add("  line: 12");
            lines.Add("  excerpt: \"if (i < count)\"");
            lines.Add("  claim: The loop bound excludes the final element.");
            lines.Add("  evidence: src/Foo.cs");
            lines.Add("  failure_scenario: The last element is never processed.");
            lines.Add("  suggestion: Use <= or count + 1.");
        }

        lines.Add("```");
        lines.Add("Discovery: codegraph 2, docs 0, direct reads 0");
        lines.Add("REVIEW.md sections checked: [Ground rules]");
        return string.Join('\n', lines) + "\n";
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
        string prior = Path.Combine(_temp.Path, "prior");
        Directory.CreateDirectory(prior);
        File.WriteAllText(Path.Combine(prior, "findings.json"), content);
        WriteSeat("correctness", NoFindingsSeat("correctness"));

        CapturedConsoleExecution<int> run = Verdict(
            ["--seats", _seats, "--prior", prior, .. NotSeatedExcept("correctness", "security")]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
    }

    /// <summary>
    /// A count too large for an int is not a count: the line reads as no discovery evidence and KW-REVIEW-044 says so,
    /// rather than the seat being unreadable.
    /// </summary>
    [Fact]
    public void ADiscoveryCountThatOverflowsReadsAsNoEvidenceAndWarnsUnderKw044()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Discovery: codegraph 99999999999, docs 0, direct reads 0",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, DiscoveryMissing, "Warning");
    }

    // -----------------------------------------------------------------------------------------
    // Seat folder entries that are not a lens: named, never a crash, never read as a seat.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A file in the seat folder that cannot be a seat (its stem is empty, dotted or not a lowercase lens id) is warned
    /// about under KW-REVIEW-041, saying it was ignored, and is never parsed. The body is a contradictory seat that
    /// would be refused under KW-REVIEW-040 if anything read it, so a stray 040 proves the entry was parsed.
    /// </summary>
    [Theory]
    [InlineData(".md")]
    [InlineData(".hidden.md")]
    [InlineData("Bad Name.md")]
    public void ASeatFolderFileThatIsNotALensIsIgnoredWithAWarningAndNeverParsed(string entry)
    {
        File.WriteAllText(Path.Combine(_seats, entry), "NO FINDINGS: I found nothing.\n" + FindingSeat());

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept()]);

        AssertEntryIgnoredAndNeverParsed(run, entry, "is not named <lens>.md with a lowercase lens id");
    }

    /// <summary>
    /// A directory named like a seat file is ignored too, with a warning saying so. A readable seat inside it is not
    /// reached: the folder is not searched below its own level.
    /// </summary>
    [Fact]
    public void ADirectoryNamedAsASeatFileIsIgnoredWithAWarningAndNeverParsed()
    {
        string directory = Path.Combine(_seats, "correctness.md");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "correctness.md"), "NO FINDINGS: I found nothing.\n" + FindingSeat());

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept()]);

        AssertEntryIgnoredAndNeverParsed(run, "correctness.md", "is a directory, not a seat file");
    }

    /// <summary>
    /// The verdict exits 0 (the entry is a warning, not a refusal), names the entry in exactly one KW-REVIEW-041
    /// warning that says why and that it was ignored, and raises no KW-REVIEW-040: nothing parsed the entry.
    /// </summary>
    private static void AssertEntryIgnoredAndNeverParsed(CapturedConsoleExecution<int> run, string entry, string reason)
    {
        Assert.True(run.Result == 0, $"Expected exit 0, got {run.Result}. Output:{Environment.NewLine}{run.Output}");
        List<string> named = [.. MessagesWithCode(run.Output, LensUnaccounted)
            .Where(message => message.Contains(entry, StringComparison.Ordinal))];
        string message = Assert.Single(named);
        Assert.Contains(reason, message, StringComparison.Ordinal);
        Assert.Contains("it was ignored", message, StringComparison.Ordinal);
        AssertDiagnostic(run.Output, LensUnaccounted, "Warning", "it was ignored");
        Assert.Empty(MessagesWithCode(run.Output, SeatUnparseable));
    }

    // -----------------------------------------------------------------------------------------
    // Repeated closing lines the sum cannot read, and a repeated Index fallback line.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AMalformedRepeatedInputSitesLineRaisesKw047()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Input sites: several",
            "Input sites: 3 traced, 1 files with none",
            "Discovery: codegraph 2, docs 0, direct reads 0",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, InputSitesMissing, "Warning", "correctness");
    }

    [Fact]
    public void AnOverflowingInputSitesSumRaisesKw047()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Input sites: 2147483647 traced, 0 files with none",
            "Input sites: 1 traced, 0 files with none",
            "Discovery: codegraph 2, docs 0, direct reads 0",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, InputSitesMissing, "Warning", "correctness");
    }

    [Fact]
    public void AMalformedRepeatedDiscoveryLineRaisesKw044()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Discovery: several",
            "Discovery: codegraph 2, docs 0, direct reads 0",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, DiscoveryMissing, "Warning");
    }

    [Fact]
    public void AnOverflowingDiscoverySumRaisesKw044()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Discovery: codegraph 2147483647, docs 0, direct reads 0",
            "Discovery: codegraph 1, docs 0, direct reads 0",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, DiscoveryMissing, "Warning");
    }

    /// <summary>
    /// Two different Index fallback lines are not silently overwritten: the verdict warns under KW-REVIEW-044 that the
    /// closing line repeated, so the reader knows the seat gave two reasons and the verdict kept one of them.
    /// </summary>
    [Fact]
    public void ARepeatedIndexFallbackLineIsNotSilentlyOverwritten()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Index fallback: projectPath: not-indexed",
            "Index fallback: projectPath: error",
            "Discovery: codegraph 0, docs 0, direct reads 6",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        string message = Assert.Single(MessagesWithCode(run.Output, DiscoveryMissing), text => text.Contains("different 'Index fallback:' lines", StringComparison.Ordinal));
        Assert.Contains("'projectPath: not-indexed'", message, StringComparison.Ordinal);
        Assert.Contains("'projectPath: error'", message, StringComparison.Ordinal);
        Assert.Contains("the first is kept", message, StringComparison.Ordinal);
    }

    /// <summary>The same Index fallback line written twice is one reason, not a contradiction, so it draws no repeat warning.</summary>
    [Fact]
    public void ARepeatedIdenticalIndexFallbackLineDrawsNoRepeatWarning()
    {
        WriteSeat("correctness", FindingSeat(trailers:
        [
            "Index fallback: projectPath: not-indexed",
            "Index fallback: projectPath: not-indexed",
            "Discovery: codegraph 0, docs 0, direct reads 6",
            "REVIEW.md sections checked: [Ground rules]"
        ]));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain(MessagesWithCode(run.Output, DiscoveryMissing), text => text.Contains("different 'Index fallback:' lines", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------------------------
    // Harness.
    // -----------------------------------------------------------------------------------------

    private CapturedConsoleExecution<int> Verdict(params string[] options)
    {
        List<string> arguments = ["review", "verdict", _repo];
        arguments.AddRange(options);
        arguments.AddRange(["--format", "json"]);
        return ProcessConsoleCapture.Run(() => CreateApp().Run([.. arguments]));
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

    private void WriteSource(string content)
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "Foo.cs"), content);
    }

    private string WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(_temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string[] ReadFindingIds(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("findings").EnumerateArray()
            .Select(finding => finding.GetProperty("id").GetString() ?? string.Empty)
            .ToArray();
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
                    .WithDescription(ReviewVerdictCommand.Description);
            });
        });
        return app;
    }
}
