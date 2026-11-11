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
    // A finding whose required identity is null: dropped as incomplete, or the file refused. Never a crash, and never
    // counted as accepted.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("file")]
    [InlineData("id")]
    [InlineData("lens")]
    public void AFindingWithANullRequiredFieldIsDroppedOrRefusedNeverAccepted(string field)
    {
        string findings = WriteFindingDocument(field, null);

        CapturedConsoleExecution<int> run = Verdict("--findings", findings);

        bool refused = run.Result == Refused && MessagesWithCode(run.Output, UnreadableInput).Count > 0;
        bool dropped = run.Result == 0 && MessagesWithCode(run.Output, IncompleteFinding).Count > 0;
        Assert.True(refused || dropped, $"A null '{field}' was neither refused nor dropped. Output:{Environment.NewLine}{run.Output}");
        Assert.DoesNotContain("1 finding(s) accepted", run.Output, StringComparison.Ordinal);
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
