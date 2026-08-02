using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Export;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Emits the documentation graph as newline-delimited JSON.</summary>
public sealed class DocsGraphCommand : Command<DocsGraphSettings>
{
    public override int Execute(CommandContext context, DocsGraphSettings settings)
    {
        var report = new DiagnosticReport();
        if (!DocsCommandComposition.TryCreateLoader(settings, report, out var loader))
        {
            CommandHelpers.Finish(report, settings, "docs graph", "Document");
            return 1;
        }

        var set = loader!.Load();
        var resolver = DocsCommandComposition.CreateResolver(settings);

        if (!resolver.IsAvailable)
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(resolver.UnavailableReason ?? "CodeGraph index unavailable.")}[/] " +
                "Document-to-code join edges cannot be emitted.");
            return 1;
        }

        var result = new DocGraphExporter(resolver).Export(set, settings.Out);

        AnsiConsole.MarkupLine(
            $"[green]{result.NodeCount} nodes[/] → {Markup.Escape(result.NodesPath)}");
        AnsiConsole.MarkupLine(
            $"[green]{result.EdgeCount} edges[/] → {Markup.Escape(result.EdgesPath)}");
        return 0;
    }
}
