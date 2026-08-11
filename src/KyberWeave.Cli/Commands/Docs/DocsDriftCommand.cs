using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Entity-drift tier: resolves documented code references against CodeGraph.</summary>
public sealed class DocsDriftCommand : Command<DocsSettings>
{
    public override int Execute(CommandContext context, DocsSettings settings)
    {
        var report = new DiagnosticReport();
        if (!DocsCommandComposition.TryCreateLoader(settings, report, out var loader))
        {
            CommandHelpers.Finish(report, settings, "docs drift", "Document");
            return 1;
        }

        var set = loader!.Load();
        var resolver = DocsCommandComposition.CreateResolver(settings);
        report.AddRange(new DocDriftLinter(resolver).Validate(set).Items);

        CommandHelpers.Finish(report, settings, "docs drift", "Document");
        return report.HasErrors ? 1 : 0;
    }
}
