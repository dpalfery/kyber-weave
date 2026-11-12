using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// The verdict command's input documents, as a reviewer hands them in: empty operands, empty files, and documents whose
/// fields or entries are null or absent. Each is refused or dropped with a diagnostic that names the input, never a crash,
/// and never accepted with a null identity. Runs in process against a temporary repository.
/// </summary>
public sealed class ReviewVerdictInputTests : IDisposable
{
    private const string UnreadableInput = "KW-REVIEW-023";
    private const string IncompleteFinding = "KW-REVIEW-001";
    private const string CitedLine = "KW-REVIEW-043";
    private const int Refused = 1;

    private readonly TempDirectory _temp = new();
    private readonly string _repo;

    public ReviewVerdictInputTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n");
    }

    /// <summary>Deletes the temporary repository.</summary>
    public void Dispose()
    {
        _temp.Dispose();
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
    public void AFindingWithAConfidenceOutsideOneToTenIsRefusedNamingTheFileAndPath(string confidence)
    {
        string findings = Write("findings.json", FindingsDocument(findings: FindingJson("confidence", confidence)));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "findings.json");
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "$.findings[0].confidence");
    }

    /// <summary>The range is a boundary, not a ban: the lowest and highest confidence and every severity name still read, whatever verdict they reach.</summary>
    [Theory]
    [InlineData("confidence", "1")]
    [InlineData("confidence", "10")]
    [InlineData("severity", "\"critical\"")]
    [InlineData("severity", "\"minor\"")]
    public void AFindingAtTheEdgeOfTheRangeIsStillAccepted(string field, string value)
    {
        string findings = Write("findings.json", FindingsDocument(findings: FindingJson(field, value)));

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        // Critical findings request changes (exit 1); what matters is that the document was read.
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

    /// <summary>One otherwise-valid finding as JSON text, with <paramref name="field"/> set to the raw JSON <paramref name="rawValue"/>.</summary>
    private static string FindingJson(string field, string rawValue)
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
            ["failureScenario"] = "The last element is never processed.",
            ["suggestion"] = "Use <= or count + 1."
        };
        finding[field] = JsonNode.Parse(rawValue);
        return finding.ToJsonString();
    }

    /// <summary>
    /// A findings document as JSON text. <paramref name="member"/> is one extra raw top-level member, or a replacement list for
    /// a known one; <paramref name="findings"/> is the raw JSON of the single finding the document holds, or none.
    /// </summary>
    private static string FindingsDocument(string? member = null, string? findings = null)
    {
        string list = findings is null ? "[]" : "[" + findings + "]";
        string known = member is not null && member.StartsWith("\"findings\":", StringComparison.Ordinal) ? string.Empty : $"\"findings\":{list},";
        string changed = member is not null && member.StartsWith("\"changedPaths\":", StringComparison.Ordinal) ? string.Empty : "\"changedPaths\":[],";
        return "{\"schema\":\"kyber-weave.review-findings/v1\"," + known + changed + "\"changedLines\":0" +
               (member is null ? string.Empty : "," + member) + "}";
    }

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
        string path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private CapturedConsoleExecution<int> Verdict(params string[] options)
    {
        List<string> arguments = ["review", "verdict", _repo, .. options, "--format", "json"];
        return ProcessConsoleCapture.Run(() => CreateApp().Run([.. arguments]));
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
