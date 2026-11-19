using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review verdict</c> command over the seat folder: what a seat's line repair, discovery evidence, lens
/// coverage and cited-line checks do to the verdict, and which entries of the folder are read as seats. Runs in process
/// over the in-memory file system; no child process, no disk.
/// </summary>
public sealed partial class ReviewVerdictSeatsCommandTests
{
    private const string LineMoved = "KW-REVIEW-043";
    private const string DiscoveryMissing = "KW-REVIEW-044";
    private const string AcceptedFindings = "KW-REVIEW-046";
    private const string InputSitesMissing = "KW-REVIEW-047";

    /// <summary>
    /// A seats folder the seat seam cannot list is a seat folder that cannot be read: KW-REVIEW-040 naming the folder,
    /// not an unhandled file-system exception out of the command. The real-permission half of this behaviour is the
    /// integration case <c>ReviewVerdictSeatsCommandTests.ASeatFolderThatCannotBeListedIsRefusedUnderKw040NamingTheFolder</c>.
    /// </summary>
    [Fact]
    public void ASeatFolderTheSeamCannotListIsRefusedUnderKw040NamingTheFolder()
    {
        FakeSeatFolder seatFolder = new() { ListFailure = new UnauthorizedAccessException("denied") };
        seatFolder.Folders.Add(_seats);
        CommandApp app = CommandAppFixture.ReviewVerdict(services =>
        {
            _services.Register(services);
            services.AddSingleton<IReviewWorkTreeService>(_trees);
            services.AddSingleton<ISeatFolder>(seatFolder);
        }, _services.Ansi);

        CapturedConsoleExecution<int> run = _services.Run(
            app, ["review", "verdict", _repo, "--seats", _seats, "--format", "json"]);

        Assert.Equal(1, run.Result);
        AssertDiagnostic(run.Output, SeatUnparseable, "Error", _seats);
    }

    // -----------------------------------------------------------------------------------------
    // --drop removes a finding only with a stated reason; no option changes a severity.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DropRemovesTheFindingAndStatesItsReason()
    {
        WriteSeat("correctness", FindingSeat());
        string report = Path.Combine(_services.Root, "report.json");

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
        string report = Path.Combine(_services.Root, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        using JsonDocument document = JsonDocument.Parse(_services.Files.ReadAllText(report));
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
        string report = Path.Combine(_services.Root, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(LineMoved, run.Output, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(_services.Files.ReadAllText(report));
        Assert.Equal(4, document.RootElement.GetProperty("findings")[0].GetProperty("line").GetInt32());
    }

    [Fact]
    public void ALineBeyondTheFileEndWithNoUniqueMatchIsDroppedAsAWarning()
    {
        WriteSource("int A()\n{\n    return total;\n}\nint B()\n{\n    return total;\n}\n");
        WriteSeat("correctness", FindingSeat(line: 99, excerpt: "    return total;"));
        string report = Path.Combine(_services.Root, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        AssertDiagnostic(run.Output, LineMoved, "Warning");
        Assert.Empty(ReadFindingIds(report));
    }

    [Fact]
    public void ALineInsideTheFileIsLeftWhereItIs()
    {
        WriteSource("public int Total(int[] items)\n{\n    int total = 0;\n    return total;\n}\n");
        WriteSeat("correctness", FindingSeat(line: 3, excerpt: "    return total;"));
        string report = Path.Combine(_services.Root, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.DoesNotContain(LineMoved, run.Output, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(_services.Files.ReadAllText(report));
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

    /// <summary>
    /// With no decision model configured (no <c>arbiter</c> block, so provider <c>none</c>), the verdict and the
    /// candidate list print what they printed before the model was added: no decision-model line, nothing dropped or
    /// merged, every major finding still a candidate, and no answer file written beside the seats.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithNoDecisionModelTheVerdictIsUnchanged(bool candidates)
    {
        WriteSeat("correctness", FindingSeat(severity: "major"));
        WriteSeat("security", FindingSeat(lens: "security", id: "security/off-by-one", severity: "minor"));
        string[] args = ["--seats", _seats, .. NotSeatedExcept("correctness", "security")];

        CapturedConsoleExecution<int> run = Verdict(candidates ? [.. args, "--candidates"] : args);

        Assert.DoesNotContain("decision model", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KW-REVIEW-055", run.Output, StringComparison.Ordinal);
        Assert.Contains("Accepted critical and major findings: correctness/off-by-one.", run.Output, StringComparison.Ordinal);
        Assert.False(_services.Files.FileExists(Path.Combine(_services.Root, "decision-model.json")));
    }

    /// <summary>
    /// The bare command finds the review's newest cycle folder, reads its seats and the not-seated decisions
    /// <c>review seats</c> and the gate run recorded there, and writes the cycle's <c>findings.json</c>: the reviewer
    /// types no path and no reason.
    /// </summary>
    [Fact]
    public void TheBareCommandReadsTheNewestCycleAndItsRecordedNotSeatedLenses()
    {
        string cycle = Path.Combine(_repo, ".agent_scratchpad", "bare-task", "review", "cycle-1");
        _services.Files.CreateDirectory(Path.Combine(cycle, "seats"));
        _services.Files.WriteAllText(Path.Combine(cycle, "seats", "correctness.md"), NoFindingsSeat("correctness"));
        _services.NotSeated.Add(cycle, ReviewLensCatalogue.Names
            .Where(lens => lens != "correctness")
            .ToDictionary(lens => lens, _ => "recorded by review seats in this test", StringComparer.Ordinal));

        CapturedConsoleExecution<int> run = Verdict("--task", "bare-task");

        Assert.Equal(0, run.Result);
        Assert.Contains("recorded by review seats in this test", run.Output, StringComparison.Ordinal);
        Assert.True(_services.Files.FileExists(Path.Combine(cycle, "findings.json")));
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
        string report = Path.Combine(_services.Root, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        using JsonDocument document = JsonDocument.Parse(_services.Files.ReadAllText(report));
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
        string report = Path.Combine(_services.Root, "report.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", report, .. NotSeatedExcept()]);

        Assert.Equal(0, run.Result);
        using JsonDocument document = JsonDocument.Parse(_services.Files.ReadAllText(report));
        JsonElement finding = document.RootElement.GetProperty("findings")[0];
        Assert.Equal("reviewer", finding.GetProperty("lens").GetString());
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
        Assert.DoesNotContain(_repo, message, StringComparison.Ordinal);
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
            _ => Path.Combine(_services.Root, "outside.cs")
        };
        WriteSeat("correctness", TwoFindingsCiting(file));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("correctness")]);

        Assert.Equal(0, run.Result);
        Assert.Single(MessagesWithCode(run.Output, LineMoved));
        AssertDiagnostic(run.Output, LineMoved, "Info");
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
    // A discovery count too large for an int is not a count.
    // -----------------------------------------------------------------------------------------

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
        _services.Files.WriteAllText(Path.Combine(_seats, entry), "NO FINDINGS: I found nothing.\n" + FindingSeat());

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
        _services.Files.CreateDirectory(directory);
        _services.Files.WriteAllText(
            Path.Combine(directory, "correctness.md"), "NO FINDINGS: I found nothing.\n" + FindingSeat());

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
}
