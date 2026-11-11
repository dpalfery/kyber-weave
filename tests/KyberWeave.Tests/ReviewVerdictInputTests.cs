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
    /// The null-element check walks the whole parsed tree, not only the lists the report model names: a null inside a
    /// list nested in an object, or in a list inside a list item, refuses the document and names the null's path.
    /// </summary>
    [Theory]
    [InlineData("\"coverage\":[{\"files\":[null]}]", "$.coverage[0].files[0]")]
    [InlineData("\"extra\":{\"inner\":{\"names\":[\"a\",null]}}", "$.extra.inner.names[1]")]
    [InlineData("\"extra\":[[\"a\"],[null]]", "$.extra[1][0]")]
    public void ANullElementNestedDeepInAFindingsDocumentIsRefusedNamingItsPath(string member, string path)
    {
        string findings = Write(
            "findings.json",
            "{\"schema\":\"kyber-weave.review-findings/v1\",\"findings\":[],\"changedPaths\":[],\"changedLines\":0," + member + "}");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", path);
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

    /// <summary>A null in a list nested inside a gate report's object is refused, naming the gate report and the null's path.</summary>
    [Fact]
    public void ANullElementNestedDeepInAGateReportIsRefusedNamingItsPath()
    {
        string findings = WriteFindingDocument("id", "correctness/off-by-one");
        string gates = Write("gates.json", "{\"schema\":\"kyber-weave.review-gates/v1\",\"gates\":[],\"coverageReports\":[{\"files\":[null]}]}");

        CapturedConsoleExecution<int> run = Verdict("--findings", findings, "--gates", gates);

        Assert.Equal(Refused, run.Result);
        AssertDiagnostic(run.Output, UnreadableInput, "Error", "$.coverageReports[0].files[0]");
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
