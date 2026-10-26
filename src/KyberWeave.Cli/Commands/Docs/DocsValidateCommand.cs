using System.ComponentModel;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Schema tier: frontmatter conformance. Needs no code index.</summary>
public sealed class DocsValidateCommand : Command<DocsValidateSettings>
{
    /// <summary>
    /// Runs every frontmatter and inventory validator, and the open-work gate when
    /// <see cref="DocsValidateSettings.MergeReady"/> is set. Exits 1 on any error.
    /// </summary>
    public override int Execute(CommandContext context, DocsValidateSettings settings,
        CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!DocsCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config,
                out OntologyConfig ontology))
        {
            CommandHelpers.Finish(report, settings, "docs validate", "Document");
            return 1;
        }

        DocumentSet set = new DocumentLoader(settings.Path, ontology).Load();
        report.AddRange(new DocSpecValidator(settings.Path, ontology).Validate(set).Items);
        report.AddRange(new ConfigRegValidator(settings.Path, config.WithOntology(ontology)).Validate().Items);
        report.AddRange(new PlanInventoryValidator(config.WithOntology(ontology)).Validate(set).Items);
        report.AddRange(new TodoInventoryValidator(config.WithOntology(ontology)).Validate(set).Items);
        if (settings.MergeReady)
        {
            report.AddRange(new OpenWorkValidator(config.WithOntology(ontology)).Validate(set).Items);
            report.AddRange(ValidateCloseout(settings, config.WithOntology(ontology), set).Items);
        }

        try
        {
            report.AddRange(new ManagedGlossaryService(
                settings.Path,
                config,
                TimeProvider.System).Validate().Items);
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            report.Add(new Diagnostic(
                ManagedGlossaryService.ValidationRuleCode,
                Severity.Error,
                exception.Message,
                "glossary",
                Hint: "Fix the managed glossary path or contents, then re-run docs validate."));
        }

        CommandHelpers.Finish(report, settings, "docs validate", "Document");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, DocsValidateSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>
    /// Runs the archive-closeout gate over the change the working tree carries: the merge
    /// base of <c>--base</c> (or the remote default branch) and <c>HEAD</c> selects the
    /// archived entries, and the gate checks each one. Scope that cannot resolve fails
    /// closed instead of reporting a clean run.
    /// </summary>
    private static DiagnosticReport ValidateCloseout(
        DocsValidateSettings settings,
        KyberWeaveConfig config,
        DocumentSet set)
    {
        DiagnosticReport report = new();

        if (!string.IsNullOrWhiteSpace(settings.Base))
        {
            string? refusal = ChangedFiles.ValidateBase(settings.Base);
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

        // The default is the remote default branch, not HEAD: a change committed on top of
        // the fetched branch is invisible against HEAD and visible against origin/HEAD.
        string baseRef = string.IsNullOrWhiteSpace(settings.Base) ? "origin/HEAD" : settings.Base;
        IReadOnlyList<ChangedPath> changes;
        try
        {
            changes = ChangedFiles.ResolveSinceMergeBase(settings.Path, baseRef);
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
            settings.Path,
            config,
            () => new DocumentLoader(settings.Path, config.Ontology)).Validate(set, changes).Items);
        return report;
    }
}
