using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Schema tier: frontmatter conformance. Needs no code index.</summary>
public sealed class DocsValidateCommand : Command<DocsSettings>
{
    public override int Execute(CommandContext context, DocsSettings settings)
    {
        var report = new DiagnosticReport();
        if (!DocsCommandComposition.TryResolveConfig(settings, report, out var config, out var ontology))
        {
            CommandHelpers.Finish(report, settings, "docs validate", "Document");
            return 1;
        }

        var set = new DocumentLoader(settings.Path, ontology).Load();
        report.AddRange(new DocSpecValidator(settings.Path, ontology).Validate(set).Items);
        report.AddRange(new ManagedGlossaryService(
            settings.Path,
            config,
            TimeProvider.System).Validate().Items);

        CommandHelpers.Finish(report, settings, "docs validate", "Document");
        return report.HasErrors ? 1 : 0;
    }
}
