using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Review;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

using KyberWeave.Tests;

namespace KyberWeave.Cli.Tests.Commands.Docs;

/// <summary>
/// Pins the public documentation-analysis command surface independently of concrete graph,
/// persistence, and embedding adapters. Composition has a separate contract below this file.
/// </summary>
public sealed class DocsAnalysisCliCommandTests
{
    private readonly MemoryDocsHost _host = new();

    private static TestConsole NonTerminalConsole() => new();

    [Fact]
    public void AnalyzeSettingsDefaultToAdvisoryAndRetainEveryExistingFormat()
    {
        DocsIntegrityCheckSettings settings = new DocsIntegrityCheckSettings();

        Assert.Equal("none", settings.FailOn);
        Assert.Null(settings.Format);
        Assert.Equal(OutputFormat.Lines, settings.FormatFor(NonTerminalConsole()));
        Assert.Equal(OutputFormat.Table, new DocsIntegrityCheckSettings { Format = "table" }.FormatFor(NonTerminalConsole()));
        Assert.Equal(OutputFormat.Json,
            new DocsIntegrityCheckSettings { Format = "json" }.FormatFor(NonTerminalConsole()));
        Assert.Equal(OutputFormat.Sarif,
            new DocsIntegrityCheckSettings { Format = "sarif" }.FormatFor(NonTerminalConsole()));
        Assert.Equal(OutputFormat.Markdown,
            new DocsIntegrityCheckSettings { Format = "markdown" }.FormatFor(NonTerminalConsole()));
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
    public async Task AnalyzeFindingExitGateHonorsFailOn(
        string failOn,
        Severity severity,
        int expectedExitCode)
    {
        RecordingCommandService service = new RecordingCommandService
        {
            AnalysisResult = AnalysisResult(Finding(severity))
        };
        DocsIntegrityCheckCommand command = new DocsIntegrityCheckCommand(service, _host.Console);

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new DocsIntegrityCheckSettings
            {
                Path = _host.Repo,
                FailOn = failOn,
                Format = "json"
            },
            CancellationToken.None));

        Assert.Equal(expectedExitCode, execution.ExitCode);
        Assert.Contains("KW-DOC-ANALYSIS-TEST", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeOperationalFailureIsNonzeroEvenWhenFailOnNone()
    {
        RecordingCommandService service = new RecordingCommandService
        {
            AnalysisException = new InvalidDataException(
                "KW-DOC-ANALYSIS-004: malformed ignore markup")
        };

        CommandExecution execution = await CaptureAsync(() => new DocsIntegrityCheckCommand(service, _host.Console).ExecuteAsync(
            null!,
            new DocsIntegrityCheckSettings
            {
                Path = _host.Repo,
                FailOn = "none",
                Format = "json"
            },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KW-DOC-ANALYSIS-004", execution.Output, StringComparison.Ordinal);
        Assert.Contains("malformed ignore markup", execution.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DocumentationAnalyzer.IgnoreMarkupRuleCode)]
    [InlineData(DocumentationAnalyzer.EmbeddingUnavailableRuleCode)]
    public async Task AnalyzeOperationalErrorDiagnosticIsNonzeroEvenWhenFailOnNone(string ruleCode)
    {
        DiagnosticReport report = new DiagnosticReport();
        report.Add(new Diagnostic(
            ruleCode,
            Severity.Error,
            "Operational analysis failure.",
            "docs analysis"));
        RecordingCommandService service = new RecordingCommandService { AnalysisResult = AnalysisResult(report) };

        CommandExecution execution = await CaptureAsync(() => new DocsIntegrityCheckCommand(service, _host.Console).ExecuteAsync(
            null!,
            new DocsIntegrityCheckSettings
            {
                Path = _host.Repo,
                FailOn = "none",
                Format = "json"
            },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains(ruleCode, execution.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("table", "(+1 related)")]
    [InlineData("json", "docs/related.md")]
    [InlineData("sarif", "docs/related.md")]
    [InlineData("markdown", "docs/related.md")]
    public async Task AnalyzeEveryFormatReportsRelatedLocationsAndMetrics(
        string format,
        string relatedMarker)
    {
        DiagnosticReport report = new DiagnosticReport();
        report.Add(FindingDiagnostic(Severity.Warning));
        report.AddMetric("extractedClaims", 17);
        report.AddMetric("truncated", false);
        RecordingCommandService service = new RecordingCommandService { AnalysisResult = AnalysisResult(report) };

        CommandExecution execution = await CaptureAsync(() => new DocsIntegrityCheckCommand(service, _host.Console).ExecuteAsync(
            null!,
            new DocsIntegrityCheckSettings
            {
                Path = _host.Repo,
                FailOn = "none",
                Format = format
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains(relatedMarker, execution.Output, StringComparison.Ordinal);
        Assert.Contains("extractedClaims", execution.Output, StringComparison.Ordinal);
        Assert.Contains("17", execution.Output, StringComparison.Ordinal);
        Assert.Contains("truncated", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewExportWritesTheCompleteBundleToTheRequestedPath()
    {
        string output = Path.Combine(_host.Repo, "review", "candidates.json");
        RecordingCommandService service = new RecordingCommandService { ExportResult = ExportResult("{\"schema\":\"candidates/v1\"}") };

        CommandExecution execution = await CaptureAsync(() => new DocsReviewExportCommand(service, _host.Files, _host.Console).ExecuteAsync(
            null!,
            new DocsReviewExportSettings { Path = _host.Repo, OutputPath = output },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(service.ExportResult.Json, _host.Files.ReadAllText(output));
    }

    [Fact]
    public async Task ReviewExportOperationalFailureLeavesAnExistingOutputByteForByteUnchanged()
    {
        string output = Path.Combine(_host.Repo, "candidates.json");
        const string sentinel = "operator-owned output";
        _host.Files.CreateDirectory(_host.Repo);
        _host.Files.WriteAllText(output, sentinel);
        RecordingCommandService service = new RecordingCommandService
        {
            ExportException = new IOException("candidate export failed")
        };

        CommandExecution execution = await CaptureAsync(() => new DocsReviewExportCommand(service, _host.Files, _host.Console).ExecuteAsync(
            null!,
            new DocsReviewExportSettings { Path = _host.Repo, OutputPath = output },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.Equal(sentinel, _host.Files.ReadAllText(output));
    }

    [Fact]
    public async Task ReviewImportReadsTheRequestedBundleAndReturnsFailureWithoutPartialWrites()
    {
        string input = Path.Combine(_host.Repo, "verdicts.json");
        const string verdicts = "{\"schema\":\"kyber-weave.docs-review.verdicts/v1\"}";
        _host.Files.WriteAllText(input, verdicts);
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

        CommandExecution execution = await CaptureAsync(() => new DocsReviewImportCommand(service, _host.Files, _host.Console).ExecuteAsync(
            null!,
            new DocsReviewImportSettings
            {
                Path = _host.Repo,
                InputPath = input,
                Format = "json"
            },
            CancellationToken.None));

        Assert.Equal(1, execution.ExitCode);
        Assert.Equal(verdicts, service.ImportedJson);
        Assert.Equal(0, service.PersistedVerdictCount);
        Assert.Contains(DocumentationReviewExchange.ReviewRuleCode, execution.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlossaryPreviewAndWritePassTheExplicitMutationChoice(bool write)
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

        CommandExecution execution = await CaptureAsync(() => new DocsGlossaryCommand(service, _host.Console).ExecuteAsync(
            null!,
            new DocsGlossarySettings { Path = _host.Repo, Write = write },
            CancellationToken.None));

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
    public async Task GlossaryMachineFormatsEmitOneParseablePayloadWithoutMarkdownPrefix(string format)
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

        CommandExecution execution = await CaptureAsync(() => new DocsGlossaryCommand(service, _host.Console).ExecuteAsync(
            null!,
            new DocsGlossarySettings { Path = _host.Repo, Format = format },
            CancellationToken.None));

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
    public async Task GlossaryHumanFormatsRenderPreviewInsideTheSelectedReport(string format)
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

        CommandExecution execution = await CaptureAsync(() => new DocsGlossaryCommand(service, _host.Console).ExecuteAsync(
            null!,
            new DocsGlossarySettings { Path = _host.Repo, Format = format },
            CancellationToken.None));

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

    private async Task<CommandExecution> CaptureAsync(Func<Task<int>> execute)
    {
        int exit = await execute();
        return new CommandExecution(exit, _host.Output);
    }

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

        public Task<DocumentationAnalysisResult> AnalyzeAsync(
            DocsIntegrityCheckSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (AnalysisException is not null) throw AnalysisException;
            return Task.FromResult(AnalysisResult);
        }

        public Task<ReviewExportResult> ExportReviewAsync(
            DocsReviewExportSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (ExportException is not null) throw ExportException;
            return Task.FromResult(ExportResult);
        }

        public Task<ReviewImportResult> ImportReviewAsync(
            DocsReviewImportSettings settings,
            string json,
            CancellationToken cancellationToken = default)
        {
            ImportedJson = json;
            if (ImportResult.Success) PersistedVerdictCount = ImportResult.ImportedCount;
            return Task.FromResult(ImportResult);
        }

        public Task<GlossaryUpdateResult> UpdateGlossaryAsync(
            DocsGlossarySettings settings,
            CancellationToken cancellationToken = default)
        {
            GlossaryWriteRequested = settings.Write;
            return Task.FromResult(GlossaryResult);
        }
    }
}
