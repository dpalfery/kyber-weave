using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Export;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using Spectre.Console.Cli;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Emits the documentation graph as newline-delimited JSON.</summary>
public sealed class DocsExportGraphCommand(
    IManagedGlossaryFactory glossaries,
    ICodeGraphPortsFactory codeGraph,
    IKyberWeaveConfigRepository configs,
    IWritableFileSystem files,
    IDocumentSourceFactory documents,
    IAnsiConsole console) : Command<DocsExportGraphSettings>
{
    public override int Execute(CommandContext context, DocsExportGraphSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        if (!DocsCommandComposition.TryCreateLoader(
                configs,
                settings,
                report,
                documents,
                out IDocumentSource? loader,
                out KyberWeaveConfig config)
            || loader is null)
        {
            CommandHelpers.Finish(console, report, settings, "docs export-graph", "Document");
            return 1;
        }

        DocumentSet set = loader.Load();
        ICodeGraphResolver resolver = DocsCommandComposition.CreateResolver(settings, codeGraph);

        if (!resolver.IsAvailable)
        {
            console.MarkupLine(
                $"[red]{Markup.Escape(resolver.UnavailableReason ?? "CodeGraph index unavailable.")}[/] " +
                "Document-to-code join edges cannot be emitted.");
            return 1;
        }

        ManagedGlossaryGraphContributor glossaryContributor;
        try
        {
            ManagedGlossaryLoadResult glossary = glossaries.Open(settings.Path, config).Load();
            glossaryContributor = new ManagedGlossaryGraphContributor(glossary);
        }
        catch (Exception exception) when (DocsAnalysisCommandErrors.IsOperational(exception))
        {
            DocsAnalysisCommandErrors.Render(
                console,
                exception,
                settings,
                "docs export-graph",
                ManagedGlossary.ValidationRuleCode);
            return 1;
        }
        DocGraphExportResult result = new DocGraphExporter(resolver, files).Export(
            set,
            settings.Out,
            contributors: [glossaryContributor]);

        console.MarkupLine(
            $"[green]{result.NodeCount} nodes[/] → {Markup.Escape(result.NodesPath)}");
        console.MarkupLine(
            $"[green]{result.EdgeCount} edges[/] → {Markup.Escape(result.EdgesPath)}");
        return 0;
    }

    public int Execute(CommandContext context, DocsExportGraphSettings settings) => Execute(context, settings, CancellationToken.None);
}
