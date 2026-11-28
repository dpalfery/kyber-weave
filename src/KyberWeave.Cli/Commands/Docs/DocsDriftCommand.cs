using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Entity-drift tier: resolves documented code references against CodeGraph.</summary>
public sealed class DocsDriftCommand(
    ICodeGraphPortsFactory codeGraph,
    IKyberWeaveConfigRepository configs,
    IDocumentSourceFactory documents,
    IAnsiConsole console) : AsyncCommand<DocsSettings>
{
    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        DocsSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new DiagnosticReport();
        if (!DocsCommandComposition.TryCreateLoader(configs, settings, report, documents, out IDocumentSource? loader)
            || loader is null)
        {
            CommandHelpers.Finish(console, report, settings, "docs drift", "Document");
            return 1;
        }

        DocumentSet set = loader.Load();
        ICodeGraphResolver resolver = await DocsCommandComposition
            .CreateResolverAsync(settings, codeGraph, cancellationToken)
            .ConfigureAwait(false);
        DiagnosticReport drift = await new DocDriftLinter(resolver)
            .ValidateAsync(set, cancellationToken)
            .ConfigureAwait(false);
        report.AddRange(drift.Items);

        CommandHelpers.Finish(console, report, settings, "docs drift", "Document");
        return report.HasErrors ? 1 : 0;
    }
}
