using System.ComponentModel;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Review;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Shared schema-validation sequence for the CLI and the docs MCP server so the two
/// surfaces cannot diverge on which validators run or in what order.
/// </summary>
public static class DocsSchemaGate
{
    /// <summary>
    /// Runs every frontmatter and inventory validator, the managed glossary gate, and — only
    /// when <paramref name="mergeReady"/> is set — the open-work and archive-closeout gates.
    /// </summary>
    public static DiagnosticReport Validate(
        string repoRoot,
        KyberWeaveConfig config,
        DocumentSet set,
        Func<DocumentLoader> closeoutLoaderFactory,
        bool mergeReady,
        string? gitBase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(closeoutLoaderFactory);

        DiagnosticReport report = new();
        OntologyConfig ontology = config.Ontology;

        report.AddRange(new DocSpecValidator(repoRoot, ontology).Validate(set).Items);
        report.AddRange(new ConfigRegValidator(repoRoot, config).Validate().Items);
        report.AddRange(new PlanInventoryValidator(config).Validate(set).Items);
        report.AddRange(new TodoInventoryValidator(config).Validate(set).Items);

        try
        {
            report.AddRange(new ManagedGlossaryService(
                repoRoot,
                config,
                TimeProvider.System).Validate().Items);
        }
        catch (Exception exception) when (IsOperationalGlossaryFailure(exception))
        {
            report.Add(new Diagnostic(
                ManagedGlossaryService.ValidationRuleCode,
                Severity.Error,
                exception.Message,
                "glossary",
                Hint: "Fix the managed glossary path or contents, then re-run docs validate."));
        }

        if (mergeReady)
        {
            report.AddRange(new OpenWorkValidator(config).Validate(set).Items);
            report.AddRange(ValidateCloseout(repoRoot, config, set, closeoutLoaderFactory, gitBase).Items);
        }

        return report;
    }

    private static bool IsOperationalGlossaryFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or
        InvalidOperationException or ArgumentException;

    /// <summary>
    /// Runs the archive-closeout gate over the change the working tree carries. Scope that
    /// cannot resolve fails closed instead of reporting a clean run.
    /// </summary>
    private static DiagnosticReport ValidateCloseout(
        string repoRoot,
        KyberWeaveConfig config,
        DocumentSet set,
        Func<DocumentLoader> closeoutLoaderFactory,
        string? gitBase)
    {
        DiagnosticReport report = new();

        if (!string.IsNullOrWhiteSpace(gitBase))
        {
            string? refusal = ChangedFiles.ValidateBase(gitBase);
            if (refusal is not null)
            {
                report.Add(new Diagnostic(
                    ArchiveCloseoutValidator.MissingCloseoutEvidence,
                    Severity.Error,
                    $"Archive closeout scope is unknown (C0): {refusal}",
                    "archive",
                    Hint: "Name a git ref such as HEAD or origin/main."));
                return report;
            }
        }

        string baseRef = string.IsNullOrWhiteSpace(gitBase) ? "origin/HEAD" : gitBase;
        IReadOnlyList<ChangedPath> changes;
        try
        {
            changes = ChangedFiles.ResolveSinceMergeBase(repoRoot, baseRef);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException or UnauthorizedAccessException
            or Win32Exception or TimeoutException)
        {
            report.Add(new Diagnostic(
                ArchiveCloseoutValidator.MissingCloseoutEvidence,
                Severity.Error,
                $"Archive closeout scope is unknown (C0): {ex.Message}",
                "archive",
                Hint: "Fetch the full history ('git fetch --unshallow' or 'git fetch origin') and re-run."));
            return report;
        }

        report.AddRange(new ArchiveCloseoutValidator(
            repoRoot,
            config,
            closeoutLoaderFactory).Validate(set, changes).Items);
        return report;
    }
}
