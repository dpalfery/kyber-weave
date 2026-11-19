using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Review;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;
using static KyberWeave.Tests.ReviewInputDocuments;

namespace KyberWeave.Tests;

/// <summary>
/// The verdict command's input documents, as a reviewer hands them in: empty operands, empty files, and documents whose
/// fields or entries are null or absent. Each is refused or dropped with a diagnostic that names the input, never a crash,
/// and never accepted with a null identity. Runs in process over the in-memory file system and fakes.
/// </summary>
public sealed class ReviewVerdictInputTests : IDisposable
{
    private const string UnreadableInput = "KW-REVIEW-023";
    private const string IncompleteFinding = "KW-REVIEW-001";
    private const string CitedLine = "KW-REVIEW-043";
    private const int Refused = 1;

    private readonly InMemoryReviewServices _services = new();
    private readonly string _repo;
    private readonly string _inputs;

    public ReviewVerdictInputTests()
    {
        _repo = Path.Combine(_services.Root, "repo");
        _services.Files.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _services.Files.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");
        _inputs = Path.Combine(_services.Root, "inputs");
        _services.Files.CreateDirectory(_inputs);
    }

    /// <summary>Releases the in-memory services.</summary>
    public void Dispose()
    {
        _services.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // A finding whose required identity is null: the findings document is refused, naming the field. The verdict never
    // drops it as incomplete or counts it as accepted, because the identity is missing from the document, not the seat.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("file")]
    [InlineData("id")]
    [InlineData("lens")]
    public void AFindingWithANullRequiredFieldRefusesTheDocumentNamingTheField(string field)
    {
        string findings = WriteFindingDocument(field, null);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", $"'{field}'");
        Assert.Empty(MessagesWithCode(run.Output, IncompleteFinding));
    }

    /// <summary>
    /// A cited path with a NUL is unverifiable, not a crash: the verdict still computes, and the unverified citation is
    /// reported once as KW-REVIEW-043 at info.
    /// </summary>
    [Fact]
    public void ACitedPathHoldingANulIsReportedUnverifiedNotCrashed()
    {
        string findings = WriteFindingDocument("file", "src/\u0000Foo.cs");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(0, run.Result);
        AssertDiagnostic(run.Output, CitedLine, "Info");
    }

    // -----------------------------------------------------------------------------------------
    // Empty and whitespace-only files: refused naming the file.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    public void AnEmptyGatesFileIsRefusedNamingTheFile(string content)
    {
        string findings = WriteFindingDocument("id", "correctness/off-by-one");
        string gates = Write("gates.json", content);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", gates);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "gates.json");
    }

    // -----------------------------------------------------------------------------------------
    // Documents whose list is null or holds a null entry: refused as unreadable.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[null],\"changedPaths\":[],\"changedLines\":0}")]
    [InlineData("{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":null,\"changedPaths\":[],\"changedLines\":0}")]
    public void AFindingsDocumentWithANullEntryOrListIsRefused(string document)
    {
        string findings = Write("findings.json", document);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error");
    }

    [Theory]
    [InlineData("{\"schema\":\"kyber-weave.review-gates/v1\",\"gates\":[null]}")]
    [InlineData("{\"schema\":\"kyber-weave.review-gates/v1\",\"gates\":null}")]
    public void AGateReportWithANullEntryOrListIsRefused(string document)
    {
        string findings = WriteFindingDocument("id", "correctness/off-by-one");
        string gates = Write("gates.json", document);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", gates);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "gates.json");
    }

    // -----------------------------------------------------------------------------------------
    // A null element in any list of a findings or gates document refuses the document, naming the file, however deeply
    // the list is nested. Each of these crashed or was read as a clean report.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[],\"changedPaths\":[null],\"changedLines\":0}")]
    [InlineData("{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[],\"changedPaths\":[],\"changedLines\":0,\"coverage\":[null]}")]
    public void ANullElementInAFindingsDocumentListIsRefusedNamingTheFile(string document)
    {
        string findings = Write("findings.json", document);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
    }

    /// <summary>
    /// A null inside a list the reader consumes is refused naming its path, however deep that list sits in the
    /// document: the null would bind as a null entry and every reader downstream would have to guard it.
    /// </summary>
    [Theory]
    [InlineData("\"changedPaths\":[\"a\",null]", "$.changedPaths[1]")]
    [InlineData("\"coverage\":[null]", "$.coverage[0]")]
    [InlineData("\"findings\":[null]", "$.findings[0]")]
    public void ANullElementInAListTheReaderConsumesIsRefusedNamingItsPath(string member, string path)
    {
        string findings = Write("findings.json", FindingsDocument(member));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", path);
    }

    /// <summary>
    /// Unknown properties are tolerated on read, because a lens may carry more than the engine consumes. A null element in
    /// a list nobody reads therefore does not refuse the document: the walk covers the lists the schema consumes, not every
    /// array the file happens to hold. Each of these was refused as KW-REVIEW-023 although nothing read the list.
    /// </summary>
    [Theory]
    [InlineData("\"extra\":[1,null]")]
    [InlineData("\"extra\":[[null]]")]
    [InlineData("\"x\":{\"y\":[1,null]}")]
    [InlineData("\"extra\":{\"inner\":{\"names\":[\"a\",null]}}")]
    [InlineData("\"coverage\":[{\"lens\":\"correctness\",\"outcome\":\"noFindings\",\"reason\":null,\"files\":[null]}]")]
    public void ANullElementInAnUnknownPropertyOfAFindingsDocumentIsTolerated(string member)
    {
        string findings = Write("findings.json", FindingsDocument(member));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
    }

    /// <summary>An unknown list on a finding itself is tolerated the same way as one on the document.</summary>
    [Fact]
    public void ANullElementInAnUnknownPropertyOfAFindingIsTolerated()
    {
        string findings = Write(
            "findings.json",
            "{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[" + FindingJson("tags", "[null]") +
            "],\"changedPaths\":[],\"changedLines\":0}");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
    }

    [Fact]
    public void ANullElementInAnUnknownPropertyOfAGateReportIsTolerated()
    {
        string findings = WriteFindingDocument("id", "correctness/off-by-one");
        string gates = Write(
            "gates.json",
            "{\"schema\":\"kyber-weave.review-gates/v1\",\"gates\":[],\"extra\":[1,null]," +
            "\"coverageReports\":[{\"path\":\"a.xml\",\"coverage\":{\"fileLinePercent\":1,\"classLinePercent\":1},\"files\":[null]}]}");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", gates);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
    }

    // -----------------------------------------------------------------------------------------
    // Range checks on a finding read from JSON. The seat path refuses a severity outside critical, major and minor and a
    // confidence outside 1 to 10 (KW-REVIEW-040); the JSON path must hold the same line, because an out-of-range value
    // reaches the engine as a real severity or a real score otherwise.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"nit\"")]
    [InlineData("\"blocker\"")]
    [InlineData("\"\"")]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("true")]
    public void AFindingWithASeverityOutsideTheEnginesNamesIsRefusedNamingTheFileAndPath(string severity)
    {
        string findings = Write("findings.json", FindingsDocument(findings: FindingJson("severity", severity)));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "$.findings[0].severity");
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-3")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("1.5")]
    [InlineData("\"9\"")]
    [InlineData("9.0")]
    [InlineData("1e1")]
    [InlineData("true")]
    [InlineData("null")]
    public void AFindingWithAConfidenceOutsideOneToTenIsRefusedNamingTheFileAndPath(string confidence)
    {
        string findings = Write("findings.json", FindingsDocument(findings: FindingJson("confidence", confidence)));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "$.findings[0].confidence");
    }

    /// <summary>
    /// The range is a boundary, not a ban: the lowest and highest confidence and every severity name still read, whatever
    /// verdict they reach. The finding must come out the other side of the reader with its values intact.
    /// </summary>
    [Theory]
    [InlineData("confidence", "1", "major", 1, "APPROVE — risk LOW, 0 finding(s) accepted, 1 dropped")]
    [InlineData("confidence", "10", "major", 10, "APPROVE — risk MEDIUM, 1 finding(s) accepted, 0 dropped")]
    [InlineData("severity", "\"critical\"", "critical", 9, "REQUEST CHANGES — risk HIGH, 1 finding(s) accepted")]
    [InlineData("severity", "\"minor\"", "minor", 9, "APPROVE — risk")]
    public void AFindingAtTheEdgeOfTheRangeIsStillAccepted(
        string field, string value, string severity, int confidence, string verdictStart)
    {
        string findings = Write("findings.json", FindingsDocument(findings: FindingJson(field, value)));
        string report = Path.Combine(_inputs, "report.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--out", report);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        Assert.Contains(MessagesWithCode(run.Output, "KW-REVIEW-024"), message =>
            message.StartsWith(verdictStart, StringComparison.Ordinal));
        using JsonDocument written = JsonDocument.Parse(_services.Files.ReadAllText(report));
        JsonElement finding = written.RootElement.GetProperty("findings")[0];
        Assert.Equal("correctness/off-by-one", finding.GetProperty("id").GetString());
        Assert.Equal(severity, finding.GetProperty("severity").GetString(), ignoreCase: true);
        Assert.Equal(confidence, finding.GetProperty("confidence").GetInt32());
    }

    /// <summary>
    /// A second spelling of the severity beside a valid one is still a severity: the serializer reads names ignoring case, so
    /// a <c>"Severity": 7</c> next to <c>"severity": "major"</c> is the value that would bind.
    /// </summary>
    [Theory]
    [InlineData("Severity", "7")]
    [InlineData("SEVERITY", "\"nit\"")]
    [InlineData("Confidence", "999")]
    [InlineData("CONFIDENCE", "0")]
    public void ADuplicateSpellingOfASeverityOrConfidenceIsHeldToTheRangeToo(string field, string rawValue)
    {
        string findings = Write("findings.json", FindingsDocument(findings: FindingJson(field, rawValue)));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        Assert.Contains(MessagesWithCode(run.Output, UnreadableInput), message =>
            message.Contains($"[0].{field}", StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------------------------
    // The reader binds property names ignoring case, so every check that keys on a name must too. Each key the range check
    // or the null walk visits is run in lowercase, PascalCase and UPPERCASE spellings.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ReviewInputDocuments.RangeSpellings), MemberType = typeof(ReviewInputDocuments))]
    public void AnOutOfRangeFindingIsRefusedWhateverTheCaseOfTheListAndFieldKeys(
        string listKind, string fieldKind, string field, string rawValue)
    {
        JsonObject finding = FindingObject(field, rawValue);
        RenameKey(finding, field, Spell(field, fieldKind));
        string findings = Write("findings.json", FindingsObject("findings", listKind, new JsonArray(finding)));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        Assert.Contains(MessagesWithCode(run.Output, UnreadableInput), message =>
            message.Contains($"findings[0].{field}", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(ReviewInputDocuments.KnownFindingsLists), MemberType = typeof(ReviewInputDocuments))]
    public void ANullElementInAKnownFindingsListIsRefusedWhateverTheCaseOfItsKey(string key, string kind)
    {
        string findings = Write("findings.json", FindingsObject(key, kind, new JsonArray("a", null)));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        AssertDiagnostic(run.Output, UnreadableInput, "Error", $"$.{Spell(key, kind)}[1]");
    }

    [Theory]
    [MemberData(nameof(ReviewInputDocuments.KnownGateLists), MemberType = typeof(ReviewInputDocuments))]
    public void ANullElementInAKnownGateListIsRefusedWhateverTheCaseOfItsKey(string key, string kind)
    {
        string findings = WriteFindingDocument("id", "correctness/off-by-one");
        JsonObject gates = new() { ["schema"] = "kyber-weave.review-gates/v1", ["gates"] = new JsonArray() };
        gates.Remove(key);
        gates[Spell(key, kind)] = new JsonArray(null, null);
        string path = Write("gates.json", gates.ToJsonString());

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", path);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "gates.json");
        AssertDiagnostic(run.Output, UnreadableInput, "Error", $"$.{Spell(key, kind)}[0]");
    }

    /// <summary>
    /// An unknown property stays unknown in any spelling: a null inside it does not refuse the document, and the finding
    /// beside it is read.
    /// </summary>
    [Theory]
    [InlineData("lower")]
    [InlineData("Pascal")]
    [InlineData("UPPER")]
    public void ANullElementInAnUnknownPropertyIsToleratedWhateverTheCaseOfTheKeys(string kind)
    {
        JsonObject finding = FindingObject("severity", "\"minor\"");
        finding[Spell("tags", kind)] = new JsonArray(1, null);
        JsonObject document = JsonNode.Parse(FindingsObject("findings", kind, new JsonArray(finding)))!.AsObject();
        document[Spell("extra", kind)] = new JsonArray(1, null);
        string findings = Write("findings.json", document.ToJsonString());
        string report = Path.Combine(_inputs, "report.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--out", report);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        using JsonDocument written = JsonDocument.Parse(_services.Files.ReadAllText(report));
        Assert.Equal(1, written.RootElement.GetProperty("findings").GetArrayLength());
    }

    // -----------------------------------------------------------------------------------------
    // Two property names equal ignoring case in one object: the serializer binds names ignoring case, so which value wins
    // is an accident of order. Refused naming the file, the object's JSON path and both spellings.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ReviewInputDocuments.DuplicateFindingsKeys), MemberType = typeof(ReviewInputDocuments))]
    public void ATwoSpellingObjectInAFindingsDocumentIsRefusedNamingTheFilePathAndBothSpellings(
        string caseName, string path, string first, string second)
    {
        string findings = Write("findings.json", DuplicateFindingsDocument(caseName));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        AssertRefusedForDuplicate(run, "findings.json", path, first, second);
    }

    [Theory]
    [MemberData(nameof(ReviewInputDocuments.DuplicateGateKeys), MemberType = typeof(ReviewInputDocuments))]
    public void ATwoSpellingObjectInAGateReportIsRefusedNamingTheFilePathAndBothSpellings(
        string caseName, string path, string first, string second)
    {
        string findings = WriteFindingDocument("id", "correctness/off-by-one");
        string gates = Write("gates.json", DuplicateGateDocument(caseName));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", gates);

        AssertRefusedForDuplicate(run, "gates.json", path, first, second);
    }

    /// <summary>A name written twice with exactly the same spelling is the same defect, wherever the JSON reader lets it through.</summary>
    [Theory]
    [InlineData("{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[],\"changedPaths\":[],\"changedLines\":0,\"changedLines\":1}")]
    [InlineData("{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[{\"id\":\"a/b\",\"lens\":\"a\",\"severity\":\"major\",\"severity\":\"critical\",\"confidence\":9,\"file\":\"f\",\"line\":1}],\"changedPaths\":[],\"changedLines\":0}")]
    public void AnExactlyRepeatedNameIsRefusedNamingTheFile(string document)
    {
        string findings = Write("findings.json", document);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
    }

    /// <summary>Objects inside an unknown extension property are not walked: two spellings there are tolerated, as nulls are.</summary>
    [Fact]
    public void ATwoSpellingObjectInsideAnUnknownPropertyIsTolerated()
    {
        JsonObject finding = FindingObject("severity", "\"minor\"");
        finding["tags"] = JsonNode.Parse("{\"x\":1,\"X\":2}");
        JsonObject document = JsonNode.Parse(FindingsObject("findings", "lower", new JsonArray(finding)))!.AsObject();
        document["extra"] = JsonNode.Parse("{\"a\":{\"b\":1,\"B\":2},\"A\":3}");
        string findings = Write("findings.json", document.ToJsonString());
        JsonObject gate = JsonNode.Parse(DuplicateGateDocument("gate-document"))!.AsObject();
        gate.Remove("GATES");
        gate["meta"] = JsonNode.Parse("{\"x\":1,\"X\":2}");
        string gates = Write("gates.json", gate.ToJsonString());
        string report = Path.Combine(_inputs, "report.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", gates, "--out", report);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        using JsonDocument written = JsonDocument.Parse(_services.Files.ReadAllText(report));
        Assert.Equal(1, written.RootElement.GetProperty("findings").GetArrayLength());
    }

    /// <summary>One spelling of every name, in any case, is read as it always was: the rule refuses a repeat, not a spelling.</summary>
    [Theory]
    [InlineData("lower")]
    [InlineData("Pascal")]
    [InlineData("UPPER")]
    public void AFindingsDocumentWrittenEntirelyInOneSpellingIsStillRead(string kind)
    {
        string findings = Write("findings.json", SpelledFindingsDocument(kind, "minor"));
        JsonObject gate = new() { ["id"] = "build", ["blocking"] = true, ["exitCode"] = 0, ["summary"] = "ok" };
        string gates = Write("gates.json", SpellKeys(new JsonObject
        {
            ["schema"] = "kyber-weave.review-gates/v1",
            ["gates"] = new JsonArray(SpellKeys(gate, kind))
        }, kind).ToJsonString());
        string report = Path.Combine(_inputs, "report.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", gates, "--out", report);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        Assert.NotEmpty(MessagesWithCode(run.Output, "KW-REVIEW-024"));
        using JsonDocument written = JsonDocument.Parse(_services.Files.ReadAllText(report));
        Assert.Equal(
            "correctness/off-by-one",
            written.RootElement.GetProperty("findings")[0].GetProperty("id").GetString());
    }

    /// <summary>A severity value is matched by name ignoring case, as the seat path matches it.</summary>
    [Theory]
    [InlineData("MAJOR")]
    [InlineData("Major")]
    [InlineData("CRITICAL")]
    public void ASeverityNameInAnyCaseIsStillRead(string severity)
    {
        string findings = Write("findings.json", SpelledFindingsDocument("lower", severity));
        string report = Path.Combine(_inputs, "report.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--out", report);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        using JsonDocument written = JsonDocument.Parse(_services.Files.ReadAllText(report));
        Assert.Equal(1, written.RootElement.GetProperty("findings").GetArrayLength());
    }

    /// <summary>
    /// A seat's <c>failure_scenario</c> is read in any case, so a finding is never dropped as incomplete for the spelling of a
    /// field it has.
    /// </summary>
    [Theory]
    [InlineData("failure_scenario")]
    [InlineData("FAILURE_SCENARIO")]
    [InlineData("Failure_Scenario")]
    public void ASnakeCaseFailureScenarioInAnyCaseIsReadAsTheFailureScenario(string key)
    {
        JsonObject finding = FindingObject("severity", "\"minor\"");
        finding.Remove("failureScenario");
        finding[key] = "It breaks on the last element.";
        string findings = Write("findings.json", FindingsObject("findings", "lower", new JsonArray(finding)));
        string report = Path.Combine(_inputs, "report.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--out", report);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        using JsonDocument written = JsonDocument.Parse(_services.Files.ReadAllText(report));
        Assert.Equal(
            "It breaks on the last element.",
            written.RootElement.GetProperty("findings")[0].GetProperty("failureScenario").GetString());
    }

    /// <summary>
    /// Pins what the rule leaves alone. A dictionary's keys are data, not field names, so two that differ only in case are
    /// both kept; and names inside an unknown extension property are not walked, so a collision there is tolerated.
    /// </summary>
    [Fact]
    public void DictionaryKeysDifferingOnlyInCaseAreAcceptedAndKept()
    {
        string findings = Write("findings.json", FindingsDocument("\"pathLineCounts\":{\"src/a.cs\":1,\"SRC/A.CS\":2}"));
        string report = Path.Combine(_inputs, "report.json");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--out", report);

        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
        using JsonDocument written = JsonDocument.Parse(_services.Files.ReadAllText(report));
        Assert.Equal(2, written.RootElement.GetProperty("pathLineCounts").EnumerateObject().Count());
    }

    [Theory]
    [InlineData("\"extra\":{\"a\":1,\"A\":2}")]
    [InlineData("\"extra\":[{\"a\":1,\"A\":2}]")]
    [InlineData("\"extra\":{\"inner\":{\"b\":1,\"B\":2}}")]
    [InlineData("\"Extra\":{\"a\":1,\"A\":2}")]
    public void NamesCollidingInsideAnUnknownExtensionPropertyAreTolerated(string member)
    {
        string findings = Write("findings.json", FindingsDocument(member));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(0, run.Result);
        Assert.Empty(MessagesWithCode(run.Output, UnreadableInput));
    }

    // -----------------------------------------------------------------------------------------
    // Depth: a document nested past what the JSON reader allows is refused, never a crash or a stack overflow.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100)]
    [InlineData(10000)]
    public void ADocumentNestedDeeperThanTheReaderAllowsIsRefusedNotCrashed(int depth)
    {
        string nested = new string('[', depth) + new string(']', depth);
        string findings = Write("findings.json", FindingsDocument("\"extra\":" + nested));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
    }

    [Fact]
    public void ANullElementInAGateReportsNestedListIsRefusedNamingTheFile()
    {
        string findings = WriteFindingDocument("id", "correctness/off-by-one");
        string gates = Write("gates.json", "{\"schema\":\"kyber-weave.review-gates/v1\",\"gates\":[],\"coverageReports\":[null]}");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", gates);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "gates.json");
    }

    // -----------------------------------------------------------------------------------------
    // Harness.
    // -----------------------------------------------------------------------------------------

    /// <summary>A findings document holding one otherwise-valid finding, with one field replaced by <paramref name="value"/>.</summary>
    private string WriteFindingDocument(string field, string? value)
    {
        JsonObject finding = new()
        {
            ["id"] = "correctness/off-by-one",
            ["lens"] = "correctness",
            ["severity"] = "major",
            ["confidence"] = 9,
            ["file"] = "src/Foo.cs",
            ["line"] = 12,
            ["excerpt"] = "if (i < count)",
            ["claim"] = "The loop bound excludes the final element.",
            ["evidence"] = "src/Foo.cs:12",
            ["failure_scenario"] = "The last element is never processed.",
            ["suggestion"] = "Use <= or count + 1."
        };
        finding[field] = value is null ? null : JsonValue.Create(value);

        JsonObject document = new()
        {
            ["schema"] = "kyber-weave.review-findings/v1",
            ["findings"] = new JsonArray(finding),
            ["changedPaths"] = new JsonArray("src/Foo.cs"),
            ["changedLines"] = 10
        };
        return Write("findings.json", document.ToJsonString());
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_inputs, name);
        _services.Files.WriteAllText(path, content);
        return path;
    }

    private CapturedConsoleExecution<int> Verdict(params string[] options)
    {
        string[] arguments = ["review", "verdict", _repo, .. options, "--format", "json"];
        return _services.Run(CommandAppFixture.ReviewVerdict(_services.Register, _services.Ansi), arguments);
    }
}
