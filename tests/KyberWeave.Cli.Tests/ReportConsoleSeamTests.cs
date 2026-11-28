using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Cli.Commands.Skills;
using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Review;
using KyberWeave.Core.Review;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Routing;
using KyberWeave.Tests.FileSystem;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The report commands decide their format from, and write their findings to, the console they were constructed with,
/// so a unit test needs neither a terminal nor the process-wide console.
/// </summary>
public sealed class ReportConsoleSeamTests
{
    private const string FindingCode = "KW-TEST-001";

    [Theory]
    [InlineData(true, 100, true)]
    [InlineData(false, 100, false)]
    public void IsInteractive_NeedsATerminalThatCanMeasureItself(bool isTerminal, int width, bool expected)
    {
        TerminalTestConsole console = new(isTerminal, width);

        Assert.Equal(expected, ReportRenderer.IsInteractive(console));
    }

    [Fact]
    public void FormatFor_WithoutAFormat_FollowsTheConsole()
    {
        AnalysisSettings settings = new();

        Assert.Equal(OutputFormat.Table, settings.FormatFor(new TerminalTestConsole(true, 100)));
        Assert.Equal(OutputFormat.Lines, settings.FormatFor(new TerminalTestConsole(false, 100)));
    }

    [Fact]
    public void FormatFor_AnExplicitFormat_IgnoresTheConsole()
    {
        AnalysisSettings settings = new() { Format = "json" };

        Assert.Equal(OutputFormat.Json, settings.FormatFor(new TerminalTestConsole(true, 100)));
        Assert.Equal(OutputFormat.Json, settings.FormatFor(new TerminalTestConsole(false, 100)));
    }

    [Fact]
    public void SkillValidate_WritesItsFindingsToTheConsoleItWasGiven_AndExitsOneOnAnError()
    {
        TestConsole console = new();
        SkillService service = new(ReportWith(Severity.Error));

        int exitCode = new ValidateCommand(service, console).Execute(null!, new AnalysisSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains(FindingCode, console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillValidate_ACleanReport_ExitsZeroAndNamesNoFinding()
    {
        TestConsole console = new();

        int exitCode = new ValidateCommand(new SkillService(new DiagnosticReport()), console)
            .Execute(null!, new AnalysisSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain(FindingCode, console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillScan_WritesItsFindingsToTheConsoleItWasGiven()
    {
        TestConsole console = new();

        int exitCode = new ScanCommand(new SkillService(ReportWith(Severity.Critical)), console)
            .Execute(null!, new ScanSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains(FindingCode, console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewDuplicates_WritesTheReportItIsHanded_AndWritesNothingWhenThereIsNone()
    {
        TestConsole withReport = new();
        TestConsole withoutReport = new();

        int found = new ReviewDuplicatesCommand(new DuplicatesService(new ReviewCommandResult(1, ReportWith(Severity.Warning))), withReport)
            .Execute(null!, new ReviewDuplicatesSettings { Format = "json" }, CancellationToken.None);
        int refused = new ReviewDuplicatesCommand(new DuplicatesService(new ReviewCommandResult(2)), withoutReport)
            .Execute(null!, new ReviewDuplicatesSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(1, found);
        Assert.Contains(FindingCode, withReport.Output, StringComparison.Ordinal);
        Assert.Equal(2, refused);
        Assert.Equal(string.Empty, withoutReport.Output);
    }

    [Fact]
    public async Task ReviewExport_WritesTheBundleThroughTheFileSystemSeam()
    {
        InMemoryFileSystem files = new();
        string output = Rooted("out", "candidates.json");
        TestConsole console = new();

        int exitCode = await new DocsReviewExportCommand(new ExportService(ExportResult("{\"k\":1}"), null), files, console)
            .ExecuteAsync(null!, new DocsReviewExportSettings { Path = Rooted(), OutputPath = output, Format = "json" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("{\"k\":1}", files.ReadAllText(output));
    }

    [Fact]
    public async Task ReviewExport_AFailingExport_WritesNoFileAndReportsTheFindingOnTheConsole()
    {
        InMemoryFileSystem files = new();
        string output = Rooted("out", "candidates.json");
        TestConsole console = new();

        int exitCode = await new DocsReviewExportCommand(new ExportService(ExportResult("{}"), new IOException("export failed")), files, console)
            .ExecuteAsync(null!, new DocsReviewExportSettings { Path = Rooted(), OutputPath = output, Format = "json" }, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.False(files.FileExists(output));
        Assert.Contains("export failed", console.Output, StringComparison.Ordinal);
    }

    private static string Rooted(params string[] segments) =>
        Path.Combine([OperatingSystem.IsWindows() ? @"C:\kw" : "/kw", .. segments]);

    private static DiagnosticReport ReportWith(Severity severity)
    {
        DiagnosticReport report = new();
        report.Add(new Diagnostic(FindingCode, severity, "A finding.", "subject"));
        return report;
    }

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

    private sealed class SkillService(DiagnosticReport report) : ISkillGovernanceService
    {
        public DiagnosticReport Validate(string path) => report;

        public DiagnosticReport Scan(string path) => report;

        public SkillLintResult Lint(string path, int minDescriptionScore) => throw new NotSupportedException();

        public SkillSet LoadSet(string path) => throw new NotSupportedException();

        public RoutingEvalSummary? Evaluate(string evalFile, IRoutingStrategy strategy, SkillSet skills) => throw new NotSupportedException();

        public SkillPackResult Pack(string skillDirectory, string? output, bool skipValidation) => throw new NotSupportedException();

        public SkillScaffoldResult Scaffold(
            string name, string outputDirectory, string templateName, bool includeLicense, bool includeMetadata) =>
            throw new NotSupportedException();
    }

    private sealed class DuplicatesService(ReviewCommandResult result) : IReviewDuplicatesService
    {
        public ReviewCommandResult Run(ReviewDuplicatesRequest request, CancellationToken cancellationToken = default) => result;
    }

    private sealed class ExportService(ReviewExportResult result, Exception? failure) : IDocsAnalysisCommandService
    {
        public Task<DocumentationAnalysisResult> AnalyzeAsync(
            DocsIntegrityCheckSettings settings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReviewExportResult> ExportReviewAsync(
            DocsReviewExportSettings settings,
            CancellationToken cancellationToken = default) =>
            failure is null ? Task.FromResult(result) : throw failure;

        public Task<ReviewImportResult> ImportReviewAsync(
            DocsReviewImportSettings settings,
            string json,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GlossaryUpdateResult> UpdateGlossaryAsync(
            DocsGlossarySettings settings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
