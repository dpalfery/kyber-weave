using KyberWeave.Cli.Commands;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Schema tier: frontmatter conformance. Needs no code index.</summary>
public sealed class DocsValidateCommand : Command<DocsSettings>
{
    public override int Execute(CommandContext context, DocsSettings settings)
    {
        var report = new DiagnosticReport();
        if (!DocsCommandComposition.TryCreateLoader(settings, report, out var loader, out var ontology))
        {
            CommandHelpers.Finish(report, settings, "docs validate", "Document");
            return 1;
        }

        var set = loader!.Load();
        report.AddRange(new DocSpecValidator(settings.Path, ontology).Validate(set).Items);

        CommandHelpers.Finish(report, settings, "docs validate", "Document");
        return report.HasErrors ? 1 : 0;
    }
}
