using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Docs.Analysis.Review;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Injectable command boundary over repository analysis and its review artifacts.</summary>
public interface IDocsAnalysisCommandService
{
    DocumentationAnalysisResult Analyze(DocsIntegrityCheckSettings settings);
    ReviewExportResult ExportReview(DocsReviewExportSettings settings);
    ReviewImportResult ImportReview(DocsReviewImportSettings settings, string json);
    GlossaryUpdateResult UpdateGlossary(DocsGlossarySettings settings);
}

public sealed class DocsIntegrityCheckCommand : Command<DocsIntegrityCheckSettings>
{
    private readonly IDocsAnalysisCommandService _service;

    public DocsIntegrityCheckCommand() : this(new RepositoryDocsAnalysisCommandService()) { }

    internal DocsIntegrityCheckCommand(IDocsAnalysisCommandService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public override int Execute(CommandContext context, DocsIntegrityCheckSettings settings)
    {
        try
        {
            DocumentationAnalysisResult result = _service.Analyze(settings);
            CommandHelpers.Finish(result.Diagnostics, settings, "docs integrity-check", "Claim");
            if (HasOperationalErrors(result.Diagnostics)) return 1;
            return FindingExitCode(result.Diagnostics, settings.FailOn);
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DiagnosticReport report = DocsAnalysisCommandErrors.Report(exception, DocumentationAnalyzer.IgnoreMarkupRuleCode);
            CommandHelpers.Finish(report, settings, "docs integrity-check", "Claim");
            return 1;
        }
    }

    private static int FindingExitCode(DiagnosticReport report, string failOn) =>
        failOn.Trim().ToLowerInvariant() switch
        {
            "none" => 0,
            "warning" => report.Items.Any(item => item.Severity >= Severity.Warning) ? 1 : 0,
            "error" => report.HasErrors ? 1 : 0,
            _ => throw new ArgumentException("--fail-on must be none, warning, or error.", nameof(failOn))
        };

    private static bool HasOperationalErrors(DiagnosticReport report) =>
        report.Items.Any(item =>
            item.Severity is Severity.Error or Severity.Critical
            && item.Code is (
                DocumentationAnalyzer.IgnoreMarkupRuleCode or
                DocumentationAnalyzer.EmbeddingUnavailableRuleCode));
}

public sealed class DocsReviewExportCommand : Command<DocsReviewExportSettings>
{
    private readonly IDocsAnalysisCommandService _service;

    public DocsReviewExportCommand() : this(new RepositoryDocsAnalysisCommandService()) { }

    internal DocsReviewExportCommand(IDocsAnalysisCommandService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public override int Execute(CommandContext context, DocsReviewExportSettings settings)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settings.OutputPath);
            ReviewExportResult result = _service.ExportReview(settings);
            AtomicTextFile.Write(settings.OutputPath, result.Json);
            DiagnosticReport report = result.Diagnostics;
            report.AddMetric("exportedReviewCharacters", result.ExportedExcerptCharacters);
            report.AddMetric("reviewCandidates", result.Bundle.Candidates.Count);
            report.AddMetric("truncated", result.Truncated);
            CommandHelpers.Finish(report, settings, "docs review export", "Candidate");
            if (settings.ParsedFormat == OutputFormat.Table)
            {
                AnsiConsole.MarkupLine(
                    $"[green]Exported[/] {result.Bundle.Candidates.Count} review candidates to " +
                    $"[grey]{Markup.Escape(settings.OutputPath)}[/].");
            }
            return 0;
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DocsAnalysisCommandErrors.Render(exception, settings, "docs review export");
            return 1;
        }
    }
}

public sealed class DocsReviewImportCommand : Command<DocsReviewImportSettings>
{
    private readonly IDocsAnalysisCommandService _service;

    public DocsReviewImportCommand() : this(new RepositoryDocsAnalysisCommandService()) { }

    internal DocsReviewImportCommand(IDocsAnalysisCommandService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public override int Execute(CommandContext context, DocsReviewImportSettings settings)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settings.InputPath);
            ReviewImportResult result = _service.ImportReview(settings, File.ReadAllText(settings.InputPath));
            CommandHelpers.Finish(result.Diagnostics, settings, "docs review import", "Candidate");
            return result.Success ? 0 : 1;
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DocsAnalysisCommandErrors.Render(
                exception,
                settings,
                "docs review import");
            return 1;
        }
    }
}

public sealed class DocsGlossaryCommand : Command<DocsGlossarySettings>
{
    private readonly IDocsAnalysisCommandService _service;

    public DocsGlossaryCommand() : this(new RepositoryDocsAnalysisCommandService()) { }

    internal DocsGlossaryCommand(IDocsAnalysisCommandService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public override int Execute(CommandContext context, DocsGlossarySettings settings)
    {
        try
        {
            GlossaryUpdateResult result = _service.UpdateGlossary(settings);
            result.Diagnostics.AddMetric("glossaryPath", result.RelativePath);
            result.Diagnostics.AddMetric("glossaryChanged", result.Changed);
            result.Diagnostics.AddMetric("glossaryWritten", result.Written);
            result.Diagnostics.AddMetric("glossaryPreview", result.Markdown);
            CommandHelpers.Finish(result.Diagnostics, settings, "docs glossary", "Glossary");
            return result.Diagnostics.HasErrors ? 1 : 0;
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DocsAnalysisCommandErrors.Render(
                exception,
                settings,
                "docs glossary",
                ManagedGlossaryService.ValidationRuleCode);
            return 1;
        }
    }
}

internal static class DocsAnalysisCommandErrors
{
    public static bool IsOperational(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or
        InvalidOperationException or ArgumentException;

    public static DiagnosticReport Report(Exception exception, string code)
    {
        DiagnosticReport report = new DiagnosticReport();
        report.Add(new Diagnostic(CodeFrom(exception.Message) ?? code, Severity.Error, exception.Message, "docs analysis"));
        return report;
    }

    private static string? CodeFrom(string message)
    {
        if (!message.StartsWith("KW-", StringComparison.Ordinal)) return null;
        int separator = message.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 ? message[..separator] : null;
    }

    public static void Render(
        Exception exception,
        DocsSettings settings,
        string command,
        string code = DocumentationReviewExchange.ReviewRuleCode) =>
        CommandHelpers.Finish(Report(exception, code), settings, command, "Operation");
}

internal static class AtomicTextFile
{
    public static void Write(string path, string content)
    {
        string absolute = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(absolute)
            ?? throw new ArgumentException("The output path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(absolute)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, absolute, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
