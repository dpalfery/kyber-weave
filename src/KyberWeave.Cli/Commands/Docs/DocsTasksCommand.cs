using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Derives a plan's, or a specification's, task file from the plan and writes or checks it, so no one writes the
/// machine format, or its execution digest, by hand.
/// </summary>
public sealed class DocsTasksCommand : Command<DocsTasksSettings>
{
    /// <summary>Both <c>--write</c> and <c>--check</c> were given; they ask for opposite things.</summary>
    public const string ConflictingModes = "KW-DOC-TASKS-CLI-001";

    /// <summary>
    /// The model provider the packet self-containment check asks, or null to skip the check. The default is the
    /// Arbiter's configured provider when a key is available for it; tests replace it.
    /// </summary>
    public static Func<KyberWeaveConfig, IArbiterProvider?> PacketProvider { get; set; } = DefaultPacketProvider;

    /// <summary>
    /// Reads the plan named by the positional path and derives its task file. With <c>--write</c> it writes the
    /// file beside the plan, leaving it untouched when it is already exactly that. With <c>--check</c> it changes
    /// nothing and reports every way the file on disk differs. With neither it prints the derived file.
    /// Exits 0 when the file was written, is already current, or was previewed; exits 1 when the plan lacks
    /// something the file needs (the finding names the task and the label to add), when <c>--check</c> finds a
    /// difference, or on a usage or I/O error.
    /// </summary>
    public override int Execute(CommandContext context, DocsTasksSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (settings.Write && settings.Check)
        {
            report.Add(new Diagnostic(
                ConflictingModes,
                Severity.Error,
                "--write and --check cannot be used together.",
                "docs tasks",
                Hint: "Run --check to see what differs, then --write to fix it."));
            CommandHelpers.Finish(report, settings, "docs tasks", "Task file");
            return 1;
        }

        string typed = settings.Path;
        string root = PlanArguments.RepositoryRootFor(typed, settings.WorkingDirectory);
        settings.Path = root;
        if (!DocsCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config, out OntologyConfig ontology))
        {
            CommandHelpers.Finish(report, settings, "docs tasks", "Task file");
            return 1;
        }

        KyberWeaveConfig effective = config.WithOntology(ontology);
        if (!PlanArguments.TryResolve(root, effective, typed, settings.WorkingDirectory, "docs tasks", report, out string relative))
        {
            CommandHelpers.Finish(report, settings, "docs tasks", "Task file");
            return 1;
        }

        PlanArguments.Announce(report, settings, relative);
        PlanTaskFileMode mode = settings.Write ? PlanTaskFileMode.Write
            : settings.Check ? PlanTaskFileMode.Check
            : PlanTaskFileMode.Preview;

        PlanTaskFileOutcome outcome = new PlanTaskFileGenerator(root, effective).Run(relative, mode);
        report.AddRange(outcome.Report.Items);

        if (outcome.ExecutionDigest is not null)
        {
            report.AddMetric("execution-digest", outcome.ExecutionDigest);
            report.AddMetric("tasks", outcome.TaskCount);
            report.AddMetric("task-file", outcome.TaskFilePath);
            report.AddMetric("wrote", outcome.Wrote);
        }

        if (mode == PlanTaskFileMode.Check && outcome.File is not null && !report.HasErrors)
        {
            CheckPacketsAsync(effective, outcome.File, relative, report, cancellationToken).GetAwaiter().GetResult();
        }

        CommandHelpers.Finish(report, settings, "docs tasks", "Task file");

        if (ReportRenderer.IsHuman(settings.ParsedFormat) && !report.HasErrors && outcome.Json is not null)
        {
            if (mode == PlanTaskFileMode.Preview)
            {
                Console.Out.Write(outcome.Json);
            }
            else if (mode == PlanTaskFileMode.Write)
            {
                AnsiConsole.MarkupLine(
                    outcome.Wrote
                        ? $"[green]Wrote[/] {Markup.Escape(outcome.TaskFilePath)} ({outcome.TaskCount} task(s))."
                        : $"{Markup.Escape(outcome.TaskFilePath)} is already current ({outcome.TaskCount} task(s)); nothing written.");
            }
            else
            {
                AnsiConsole.MarkupLine($"{Markup.Escape(outcome.TaskFilePath)} is current ({outcome.TaskCount} task(s)).");
            }
        }

        return report.HasErrors ? 1 : 0;
    }

    private static IArbiterProvider? DefaultPacketProvider(KyberWeaveConfig config)
    {
        if (config.Arbiter.Provider.Kind == ArbiterProviderKind.None)
        {
            return null;
        }

        string endpoint = config.Arbiter.Provider.Endpoint;
        bool loopback = Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;
        if (!loopback && ArbiterCommandComposition.TryResolveKey(config, endpoint) is null)
        {
            return null;
        }

        return ArbiterCommandComposition.CreateProvider(config, null, new DiagnosticReport(), out _);
    }

    private static async Task CheckPacketsAsync(
        KyberWeaveConfig config, PlanTaskFile file, string plan, DiagnosticReport report, CancellationToken cancellationToken)
    {
        IArbiterProvider? provider = PacketProvider(config);
        if (provider is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileValidator.PacketNotSelfContained,
                Severity.Info,
                "Packet self-containment check skipped: no model provider with a key is configured.",
                "docs tasks",
                plan,
                Hint: "Set arbiter.provider.kind to systemone and store a key with 'kyber-weave arbiter setup' to have a model read each packet."));
            return;
        }

        try
        {
            report.AddRange(await PacketSelfContainment.CheckAsync(provider, file, plan, null, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }
}
