using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Schema tier: frontmatter conformance. Needs no code index.</summary>
public sealed class DocsValidateCommand : Command<DocsSettings>
{
    public override int Execute(CommandContext context, DocsSettings settings)
    {
        DiagnosticReport report = new DiagnosticReport();
        if (!DocsCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig? config, out OntologyConfig? ontology))
        {
            CommandHelpers.Finish(report, settings, "docs validate", "Document");
            return 1;
        }

        DocumentSet set = new DocumentLoader(settings.Path, ontology).Load();
        report.AddRange(new DocSpecValidator(settings.Path, ontology).Validate(set).Items);
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
}
