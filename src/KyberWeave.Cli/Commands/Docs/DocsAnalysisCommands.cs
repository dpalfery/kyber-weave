using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Repositories;
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
    Task<DocumentationAnalysisResult> AnalyzeAsync(
        DocsIntegrityCheckSettings settings,
        CancellationToken cancellationToken = default);
    Task<ReviewExportResult> ExportReviewAsync(
        DocsReviewExportSettings settings,
        CancellationToken cancellationToken = default);
    Task<ReviewImportResult> ImportReviewAsync(
        DocsReviewImportSettings settings,
        string json,
        CancellationToken cancellationToken = default);
    Task<GlossaryUpdateResult> UpdateGlossaryAsync(
        DocsGlossarySettings settings,
        CancellationToken cancellationToken = default);
}

public sealed class DocsIntegrityCheckCommand : AsyncCommand<DocsIntegrityCheckSettings>
{
    private readonly IDocsAnalysisCommandService _service;
    private readonly IAnsiConsole _console;

    public DocsIntegrityCheckCommand(IDocsAnalysisCommandService service, IAnsiConsole console)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        DocsIntegrityCheckSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            DocumentationAnalysisResult result = await _service.AnalyzeAsync(settings, cancellationToken).ConfigureAwait(false);
            CommandHelpers.Finish(_console, result.Diagnostics, settings, "docs integrity-check", "Claim");
            if (HasOperationalErrors(result.Diagnostics)) return 1;
            return FindingExitCode(result.Diagnostics, settings.FailOn);
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DiagnosticReport report = DocsAnalysisCommandErrors.Report(exception, DocumentationAnalyzer.IgnoreMarkupRuleCode);
            CommandHelpers.Finish(_console, report, settings, "docs integrity-check", "Claim");
            return 1;
        }
    }

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
    private static int FindingExitCode(DiagnosticReport report, string failOn) =>
        failOn.Trim().ToLowerInvariant() switch
        {
            "none" => 0,
            "warning" => report.Items.Any(item => item.Severity >= Severity.Warning) ? 1 : 0,
            "error" => report.HasErrors ? 1 : 0,
            _ => throw new ArgumentException("--fail-on must be none, warning, or error.", nameof(failOn))
        };
#pragma warning restore CA1308

    private static bool HasOperationalErrors(DiagnosticReport report) =>
        report.Items.Any(item =>
            item.Severity is Severity.Error or Severity.Critical
            && item.Code is (
                DocumentationAnalyzer.IgnoreMarkupRuleCode or
                DocumentationAnalyzer.EmbeddingUnavailableRuleCode));
}

public sealed class DocsReviewExportCommand : AsyncCommand<DocsReviewExportSettings>
{
    private readonly IDocsAnalysisCommandService _service;
    private readonly IWritableFileSystem _files;
    private readonly IAnsiConsole _console;

    public DocsReviewExportCommand(IDocsAnalysisCommandService service, IWritableFileSystem files, IAnsiConsole console)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        DocsReviewExportSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settings.OutputPath);
            ReviewExportResult result = await _service.ExportReviewAsync(settings, cancellationToken).ConfigureAwait(false);
            _files.WriteAllTextAtomic(settings.OutputPath, result.Json);
            DiagnosticReport report = result.Diagnostics;
            report.AddMetric("exportedReviewCharacters", result.ExportedExcerptCharacters);
            report.AddMetric("reviewCandidates", result.Bundle.Candidates.Count);
            report.AddMetric("truncated", result.Truncated);
            CommandHelpers.Finish(_console, report, settings, "docs review export", "Candidate");
            if (ReportRenderer.IsHuman(settings.FormatFor(_console)))
            {
                _console.MarkupLine(
                    $"[green]Exported[/] {result.Bundle.Candidates.Count} review candidates to " +
                    $"[grey]{Markup.Escape(settings.OutputPath)}[/].");
            }
            return 0;
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DocsAnalysisCommandErrors.Render(_console, exception, settings, "docs review export");
            return 1;
        }
    }
}

public sealed class DocsReviewImportCommand : AsyncCommand<DocsReviewImportSettings>
{
    private readonly IDocsAnalysisCommandService _service;
    private readonly IFileSystem _files;
    private readonly IAnsiConsole _console;

    public DocsReviewImportCommand(IDocsAnalysisCommandService service, IFileSystem files, IAnsiConsole console)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        DocsReviewImportSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(settings.InputPath);
            ReviewImportResult result = await _service
                .ImportReviewAsync(settings, _files.ReadAllText(settings.InputPath), cancellationToken)
                .ConfigureAwait(false);
            CommandHelpers.Finish(_console, result.Diagnostics, settings, "docs review import", "Candidate");
            return result.Success ? 0 : 1;
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DocsAnalysisCommandErrors.Render(
                _console,
                exception,
                settings,
                "docs review import");
            return 1;
        }
    }
}

public sealed class DocsGlossaryCommand : AsyncCommand<DocsGlossarySettings>
{
    private readonly IDocsAnalysisCommandService _service;
    private readonly IAnsiConsole _console;

    public DocsGlossaryCommand(IDocsAnalysisCommandService service, IAnsiConsole console)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        DocsGlossarySettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            GlossaryUpdateResult result = await _service.UpdateGlossaryAsync(settings, cancellationToken).ConfigureAwait(false);
            result.Diagnostics.AddMetric("glossaryPath", result.RelativePath);
            result.Diagnostics.AddMetric("glossaryChanged", result.Changed);
            result.Diagnostics.AddMetric("glossaryWritten", result.Written);
            result.Diagnostics.AddMetric("glossaryPreview", result.Markdown);
            CommandHelpers.Finish(_console, result.Diagnostics, settings, "docs glossary", "Glossary");
            return result.Diagnostics.HasErrors ? 1 : 0;
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DocsAnalysisCommandErrors.Render(
                _console,
                exception,
                settings,
                "docs glossary",
                ManagedGlossary.ValidationRuleCode);
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
        IAnsiConsole console,
        Exception exception,
        DocsSettings settings,
        string command,
        string code = DocumentationAnalysisRuleIds.ReviewRuleCode) =>
        CommandHelpers.Finish(console, Report(exception, code), settings, command, "Operation");
}
