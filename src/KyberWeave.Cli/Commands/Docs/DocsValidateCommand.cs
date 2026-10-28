using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
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
        KyberWeaveConfig effective = config.WithOntology(ontology);
        report.AddRange(DocsSchemaGate.Validate(
            settings.Path,
            effective,
            set,
            () => new DocumentLoader(settings.Path, ontology),
            settings.MergeReady,
            settings.Base).Items);

        CommandHelpers.Finish(report, settings, "docs validate", "Document");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, DocsValidateSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

}
