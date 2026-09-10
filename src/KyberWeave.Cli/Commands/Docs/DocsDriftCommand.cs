using KyberWeave.Core.CodeGraph;
using System.Threading;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Entity-drift tier: resolves documented code references against CodeGraph.</summary>
public sealed class DocsDriftCommand : Command<DocsSettings>
{
    protected override int Execute(CommandContext context, DocsSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new DiagnosticReport();
        if (!DocsCommandComposition.TryCreateLoader(settings, report, out DocumentLoader? loader))
        {
            CommandHelpers.Finish(report, settings, "docs drift", "Document");
            return 1;
        }

        DocumentSet set = loader!.Load();
        ICodeGraphResolver resolver = DocsCommandComposition.CreateResolver(settings);
        report.AddRange(new DocDriftLinter(resolver).Validate(set).Items);

        CommandHelpers.Finish(report, settings, "docs drift", "Document");
        return report.HasErrors ? 1 : 0;
    }

    public int Execute(CommandContext context, DocsSettings settings) => Execute(context, settings, CancellationToken.None);
}
