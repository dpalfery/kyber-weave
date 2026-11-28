using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Delivery;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Run;

/// <summary>
/// Pushes a finished run's branch and creates or updates its draft pull request on GitHub, with the description
/// assembled from what the run recorded. The same step the docs server's deliver tool runs (ADR 0081).
/// </summary>
public sealed class RunDeliverCommand(IRunDeliveryRunner delivery, IConfigLoadService configs, IPathResolver paths, IProcessEnvironment environment, IAnsiConsole console) : AsyncCommand<RunDeliverSettings>
{
    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description =
        "Push the run branch and open or update its draft pull request, described from what the run recorded.";

    /// <inheritdoc />
    public override async Task<int> ExecuteAsync(CommandContext context, RunDeliverSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        if (!configs.TryLoadWithDocsRoots(settings.Path, settings.Config, settings.DocsRoots, report, out KyberWeaveConfig config, out OntologyConfig ontology))
        {
            CommandHelpers.Finish(console, report, settings, "run deliver", "Run");
            return 1;
        }

        DeliveryOutcome outcome = await delivery.RunAsync(paths.GetFullPath(settings.Path, environment.CurrentDirectory), config.WithOntology(ontology),
            new DeliveryRequest(settings.Summary ?? string.Empty, settings.Notes, settings.Title, !settings.NoDraft,
                settings.DocumentationImpact, settings.Plan), cancellationToken).ConfigureAwait(false);
        report.AddRange(outcome.Report.Items);
        report.AddMetric("delivered", outcome.Delivered);
        if (outcome.Delivered)
        {
            report.AddMetric("created", outcome.Created);
            report.AddMetric("pull-request", outcome.Link ?? string.Empty);
            report.AddMetric("draft", outcome.IsDraft == true);
            report.AddMetric("base", outcome.Base ?? string.Empty);
            report.AddMetric("head", outcome.Head ?? string.Empty);
            report.AddMetric("checks", outcome.Checks ?? string.Empty);
        }

        CommandHelpers.Finish(console, report, settings, "run deliver", "Run");
        return report.HasErrors ? 1 : 0;
    }
}
