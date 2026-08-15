using System.Runtime.CompilerServices;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the public documentation-analysis command surface independently of concrete graph,
/// persistence, and embedding adapters. Composition has a separate contract below this file.
/// </summary>
public sealed class DocsAnalysisCliCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ProgramRegistersAnalyzeReviewAndGlossaryCommands()
    {
        string program = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "KyberWeave.Cli",
            "Program.cs"));

        Assert.Contains("AddCommand<DocsIntegrityCheckCommand>(\"integrity-check\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<DocsExportGraphCommand>(\"export-graph\")", program, StringComparison.Ordinal);
        Assert.Contains("AddBranch(\"review\"", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<DocsReviewExportCommand>(\"export\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<DocsReviewImportCommand>(\"import\")", program, StringComparison.Ordinal);
        Assert.Contains("AddCommand<DocsGlossaryCommand>(\"glossary\")", program, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeSettingsDefaultToAdvisoryAndRetainEveryExistingFormat()
    {
        DocsIntegrityCheckSettings settings = new DocsIntegrityCheckSettings();

        Assert.Equal("none", settings.FailOn);
        Assert.Equal("table", settings.Format);
        Assert.Equal(KyberWeave.Cli.Rendering.OutputFormat.Json,
            new DocsIntegrityCheckSettings { Format = "json" }.ParsedFormat);
        Assert.Equal(KyberWeave.Cli.Rendering.OutputFormat.Sarif,
            new DocsIntegrityCheckSettings { Format = "sarif" }.ParsedFormat);
        Assert.Equal(KyberWeave.Cli.Rendering.OutputFormat.Markdown,
            new DocsIntegrityCheckSettings { Format = "markdown" }.ParsedFormat);
    }

    [Theory]
    [InlineData("none", Severity.Error, 0)]
    [InlineData("none", Severity.Warning, 0)]
    [InlineData("warning", Severity.Info, 0)]
    [InlineData("warning", Severity.Warning, 1)]
    [InlineData("warning", Severity.Error, 1)]
    [InlineData("error", Severity.Warning, 0)]
    [InlineData("error", Severity.Error, 1)]
    [InlineData("error", Severity.Critical, 1)]
    public void AnalyzeFindingExitGateHonorsFailOn(
        string failOn,
        Severity severity,
        int expectedExitCode)
    {
        RecordingCommandService service = new RecordingCommandService
        {
            AnalysisResult = AnalysisResult(Finding(severity))
        };
        DocsIntegrityCheckCommand command = new DocsIntegrityCheckCommand(service);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new DocsIntegrityCheckSettings
            {
                Path = _temp.Path,
                FailOn = failOn,
                Format = "json"
            }));

        Assert.Equal(expectedExitCode, execution.ExitCode);
        Assert.Contains("KW-DOC-ANALYSIS-TEST", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeOperationalFailureIsNonzeroEvenWhenFailOnNone()
    {
        RecordingCommandService service = new RecordingCommandService
        {
            AnalysisException = new InvalidDataException(
                "KW-DOC-ANALYSIS-004: malformed ignore markup")
        };

        CommandExecution execution = Capture(() => new DocsIntegrityCheckCommand(service).Execute(
            null!,
            new DocsIntegrityCheckSettings
            {
                Path = _temp.Path,
                FailOn = "none",
                Format = "json"
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KW-DOC-ANALYSIS-004", execution.Output, StringComparison.Ordinal);
        Assert.Contains("malformed ignore markup", execution.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DocumentationAnalyzer.IgnoreMarkupRuleCode)]
    [InlineData(DocumentationAnalyzer.EmbeddingUnavailableRuleCode)]
    public void AnalyzeOperationalErrorDiagnosticIsNonzeroEvenWhenFailOnNone(string ruleCode)
    {
        DiagnosticReport report = new DiagnosticReport();
        report.Add(new Diagnostic(
            ruleCode,
            Severity.Error,
            "Operational analysis failure.",
            "docs analysis"));
        RecordingCommandService service = new RecordingCommandService { AnalysisResult = AnalysisResult(report) };

        CommandExecution execution = Capture(() => new DocsIntegrityCheckCommand(service).Execute(
            null!,
            new DocsIntegrityCheckSettings
            {
                Path = _temp.Path,
                FailOn = "none",
                Format = "json"
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains(ruleCode, execution.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("table", "(+1 related)")]
    [InlineData("json", "docs/related.md")]
    [InlineData("sarif", "docs/related.md")]
    [InlineData("markdown", "docs/related.md")]
    public void AnalyzeEveryFormatReportsRelatedLocationsAndMetrics(
        string format,
        string relatedMarker)
    {
        DiagnosticReport report = new DiagnosticReport();
        report.Add(FindingDiagnostic(Severity.Warning));
        report.AddMetric("extractedClaims", 17);
        report.AddMetric("truncated", false);
        RecordingCommandService service = new RecordingCommandService { AnalysisResult = AnalysisResult(report) };

        CommandExecution execution = Capture(() => new DocsIntegrityCheckCommand(service).Execute(
            null!,
            new DocsIntegrityCheckSettings
            {
                Path = _temp.Path,
                FailOn = "none",
                Format = format
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(relatedMarker, execution.Output, StringComparison.Ordinal);
        Assert.Contains("extractedClaims", execution.Output, StringComparison.Ordinal);
        Assert.Contains("17", execution.Output, StringComparison.Ordinal);
        Assert.Contains("truncated", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewExportWritesTheCompleteBundleToTheRequestedPath()
    {
        string output = Path.Combine(_temp.Path, "review", "candidates.json");
        RecordingCommandService service = new RecordingCommandService { ExportResult = ExportResult("{\"schema\":\"candidates/v1\"}") };

        CommandExecution execution = Capture(() => new DocsReviewExportCommand(service).Execute(
            null!,
            new DocsReviewExportSettings { Path = _temp.Path, OutputPath = output }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(service.ExportResult.Json, File.ReadAllText(output));
    }

    [Fact]
    public void ReviewExportOperationalFailureLeavesAnExistingOutputByteForByteUnchanged()
    {
        string output = Path.Combine(_temp.Path, "candidates.json");
        const string sentinel = "operator-owned output";
        File.WriteAllText(output, sentinel);
        RecordingCommandService service = new RecordingCommandService
        {
            ExportException = new IOException("candidate export failed")
        };

        CommandExecution execution = Capture(() => new DocsReviewExportCommand(service).Execute(
            null!,
            new DocsReviewExportSettings { Path = _temp.Path, OutputPath = output }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Equal(sentinel, File.ReadAllText(output));
    }

    [Fact]
    public void ReviewImportReadsTheRequestedBundleAndReturnsFailureWithoutPartialWrites()
    {
        string input = Path.Combine(_temp.Path, "verdicts.json");
        const string verdicts = "{\"schema\":\"kyber-weave.docs-review.verdicts/v1\"}";
        File.WriteAllText(input, verdicts);
        DiagnosticReport diagnostics = new DiagnosticReport();
        diagnostics.Add(new Diagnostic(
            DocumentationReviewExchange.ReviewRuleCode,
            Severity.Error,
            "The verdict bundle is stale.",
            "docs review import"));
        RecordingCommandService service = new RecordingCommandService
        {
            ImportResult = new ReviewImportResult(false, 0, diagnostics)
        };

        CommandExecution execution = Capture(() => new DocsReviewImportCommand(service).Execute(
            null!,
            new DocsReviewImportSettings
            {
                Path = _temp.Path,
                InputPath = input,
                Format = "json"
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Equal(verdicts, service.ImportedJson);
        Assert.Equal(0, service.PersistedVerdictCount);
        Assert.Contains(DocumentationReviewExchange.ReviewRuleCode, execution.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlossaryPreviewAndWritePassTheExplicitMutationChoice(bool write)
    {
        const string markdown = "## loop\n\n| Sense ID | Status | Definition | Scope | Aliases |";
        RecordingCommandService service = new RecordingCommandService
        {
            GlossaryResult = new GlossaryUpdateResult(
                "docs/glossary.md",
                markdown,
                Changed: true,
                Written: write,
                new DiagnosticReport())
        };

        CommandExecution execution = Capture(() => new DocsGlossaryCommand(service).Execute(
            null!,
            new DocsGlossarySettings { Path = _temp.Path, Write = write }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(write, service.GlossaryWriteRequested);
        if (!write)
        {
            Assert.Contains("glossaryPreview", execution.Output, StringComparison.Ordinal);
            Assert.Contains("## loop", execution.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("sarif")]
    public void GlossaryMachineFormatsEmitOneParseablePayloadWithoutMarkdownPrefix(string format)
    {
        const string markdown = "---\nid: reference/glossary\n---\n\n## loop\n";
        RecordingCommandService service = new RecordingCommandService
        {
            GlossaryResult = new GlossaryUpdateResult(
                "docs/glossary.md",
                markdown,
                Changed: true,
                Written: false,
                new DiagnosticReport())
        };

        CommandExecution execution = Capture(() => new DocsGlossaryCommand(service).Execute(
            null!,
            new DocsGlossarySettings { Path = _temp.Path, Format = format }));

        Assert.Equal(0, execution.ExitCode);
        using JsonDocument payload = JsonDocument.Parse(execution.Output);
        Assert.DoesNotContain("---\n", execution.Output[..Math.Min(20, execution.Output.Length)], StringComparison.Ordinal);
        Assert.Contains("glossaryPreview", execution.Output, StringComparison.Ordinal);
        Assert.Contains("reference/glossary", execution.Output, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, payload.RootElement.ValueKind);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("markdown")]
    public void GlossaryHumanFormatsRenderPreviewInsideTheSelectedReport(string format)
    {
        const string markdown = "## loop\n\n| Sense ID | Status | Definition | Scope | Aliases |";
        RecordingCommandService service = new RecordingCommandService
        {
            GlossaryResult = new GlossaryUpdateResult(
                "docs/glossary.md",
                markdown,
                Changed: true,
                Written: false,
                new DiagnosticReport())
        };

        CommandExecution execution = Capture(() => new DocsGlossaryCommand(service).Execute(
            null!,
            new DocsGlossarySettings { Path = _temp.Path, Format = format }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("glossaryPreview", execution.Output, StringComparison.Ordinal);
        Assert.Contains("## loop", execution.Output, StringComparison.Ordinal);
        Assert.False(execution.Output.StartsWith(markdown, StringComparison.Ordinal));
    }

    [Fact]
    public void DocsSettingsDescribePathAsRepositoryDocumentationRatherThanSkillInput()
    {
        string description = typeof(DocsSettings)
            .GetProperty(nameof(DocsSettings.Path))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: true)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

        Assert.Contains("repository", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("documentation", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SKILL.md", description, StringComparison.Ordinal);
    }

    private static DiagnosticReport Finding(Severity severity)
    {
        DiagnosticReport report = new DiagnosticReport();
        report.Add(FindingDiagnostic(severity));
        return report;
    }

    private static Diagnostic FindingDiagnostic(Severity severity) => new(
        "KW-DOC-ANALYSIS-TEST",
        severity,
        "Documentation analysis finding.",
        "claim-a",
        "docs/primary.md",
        StartLine: 10,
        EndLine: 12,
        RelatedLocations:
        [
            new DiagnosticLocation(
                "docs/related.md",
                StartLine: 21,
                EndLine: 23,
                Message: "Related claim")
        ]);

    private static DocumentationAnalysisResult AnalysisResult(DiagnosticReport report) => new(
        [],
        report,
        new AnalysisMetrics(17, 3, 4, 0, 2, 2, 0, false));

    private static ReviewExportResult ExportResult(string json) => new(
        new ReviewCandidateBundle(
            DocumentationReviewExchange.CandidateSchema,
            DocumentationAnalyzer.AnalyzerVersion,
            DocumentationAnalyzer.RubricVersion,
            "set-hash",
            new ReviewRubric([]),
            []),
        json,
        json.Length,
        Truncated: false);

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class RecordingCommandService : IDocsAnalysisCommandService
    {
        public DocumentationAnalysisResult AnalysisResult { get; init; } =
            DocsAnalysisCliCommandTests.AnalysisResult(new DiagnosticReport());
        public Exception? AnalysisException { get; init; }
        public ReviewExportResult ExportResult { get; init; } =
            DocsAnalysisCliCommandTests.ExportResult("{}");
        public Exception? ExportException { get; init; }
        public ReviewImportResult ImportResult { get; init; } =
            new(true, 0, new DiagnosticReport());
        public GlossaryUpdateResult GlossaryResult { get; init; } =
            new("docs/glossary.md", string.Empty, false, false, new DiagnosticReport());
        public string? ImportedJson { get; private set; }
        public int PersistedVerdictCount { get; private set; }
        public bool GlossaryWriteRequested { get; private set; }

        public DocumentationAnalysisResult Analyze(DocsIntegrityCheckSettings settings)
        {
            if (AnalysisException is not null) throw AnalysisException;
            return AnalysisResult;
        }

        public ReviewExportResult ExportReview(DocsReviewExportSettings settings)
        {
            if (ExportException is not null) throw ExportException;
            return ExportResult;
        }

        public ReviewImportResult ImportReview(DocsReviewImportSettings settings, string json)
        {
            ImportedJson = json;
            if (ImportResult.Success) PersistedVerdictCount = ImportResult.ImportedCount;
            return ImportResult;
        }

        public GlossaryUpdateResult UpdateGlossary(DocsGlossarySettings settings)
        {
            GlossaryWriteRequested = settings.Write;
            return GlossaryResult;
        }
    }
}
