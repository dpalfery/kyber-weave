using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
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
public sealed class DocsTasksCommand(
    IPlanTaskService tasks,
    IPlanTaskAgentPickerFactory pickers,
    IArbiterKeyService arbiterKeys,
    IArbiterDryRunService arbiterDryRun,
    IRepositoryFileStore files,
    IKyberWeaveConfigRepository configs,
    IAnsiConsole console,
    PlanArguments plans) : AsyncCommand<DocsTasksSettings>
{
    /// <summary>Both <c>--write</c> and <c>--check</c> were given; they ask for opposite things.</summary>
    public const string ConflictingModes = "KW-DOC-TASKS-CLI-001";

    /// <summary>
    /// The model provider the packet self-containment check asks, or null to skip the check. The default is the
    /// Arbiter's configured provider when a key is available for it; tests replace it through
    /// <see cref="PacketProviderOverride"/>.
    /// </summary>
    public static Func<KyberWeaveConfig, CancellationToken, Task<IArbiterProvider?>>? PacketProviderOverride { get; set; }

    private Task<IArbiterProvider?> PacketProviderAsync(
        KyberWeaveConfig config,
        CancellationToken cancellationToken) =>
        (PacketProviderOverride ?? DefaultPacketProviderAsync)(config, cancellationToken);

    /// <summary>
    /// Reads the plan named by the positional path and derives its task file. With <c>--write</c> it writes the
    /// file beside the plan, leaving it untouched when it is already exactly that. With <c>--check</c> it changes
    /// nothing and reports every way the file on disk differs. With neither it prints the derived file.
    /// Exits 0 when the file was written, is already current, or was previewed; exits 1 when the plan lacks
    /// something the file needs (the finding names the task and the label to add), when <c>--check</c> finds a
    /// difference, or on a usage or I/O error.
    /// </summary>
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        DocsTasksSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DiagnosticReport report = new();
        if (!ValidateModes(settings, report))
        {
            CommandHelpers.Finish(console, report, settings, "docs tasks", "Task file");
            return 1;
        }

        string typed = settings.Path;
        string root = plans.RepositoryRootFor(typed, settings.WorkingDirectory);
        settings.Path = root;
        if (!DocsCommandComposition.TryResolveConfig(configs, settings, report, out KyberWeaveConfig config, out OntologyConfig ontology))
        {
            CommandHelpers.Finish(console, report, settings, "docs tasks", "Task file");
            return 1;
        }

        KyberWeaveConfig effective = config.WithOntology(ontology);
        if (!plans.TryResolve(root, effective, files, typed, settings.WorkingDirectory, "docs tasks", report, out string relative))
        {
            CommandHelpers.Finish(console, report, settings, "docs tasks", "Task file");
            return 1;
        }

        if (!settings.List)
        {
            plans.Announce(report, settings, relative);
        }

        PlanTaskFileMode mode = settings.Write ? PlanTaskFileMode.Write
            : settings.Check ? PlanTaskFileMode.Check
            : settings.Convert ? PlanTaskFileMode.Convert
            : PlanTaskFileMode.Preview;

        // Only a write may ask the decision model to pick each task's agent; check and preview keep the picks on disk.
        using IPlanTaskAgentPicker? picker = mode is PlanTaskFileMode.Write or PlanTaskFileMode.Convert
            ? await pickers.ForConfigAsync(effective, cancellationToken).ConfigureAwait(false)
            : null;
        PlanTaskFileOutcome outcome = await tasks
            .RunAsync(root, effective, relative, mode, picker, cancellationToken)
            .ConfigureAwait(false);
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
            await CheckPacketsAsync(effective, outcome.File, relative, report, cancellationToken).ConfigureAwait(false);
        }

        if (settings.List && outcome.File is not null && !report.HasErrors)
        {
            foreach (PlanTaskFileEntry task in outcome.File.Tasks ?? [])
            {
                await console.Profile.Out.Writer
                    .WriteLineAsync(PlanTaskPacket.ListLine(task).AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }

            return 0;
        }

        CommandHelpers.Finish(console, report, settings, "docs tasks", "Task file");
        PrintOutcome(settings, mode, outcome, report);
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Refuses the flag combinations that ask for opposite things; a refusal is reported and false returned.</summary>
    private static bool ValidateModes(DocsTasksSettings settings, DiagnosticReport report)
    {
        if (settings.List && (settings.Write || settings.Check || settings.Convert))
        {
            report.Add(new Diagnostic(
                ConflictingModes,
                Severity.Error,
                "--list cannot be used with --write, --check or --convert.",
                "docs tasks",
                Hint: "Run --list alone to see the tasks, or --check, --write or --convert alone."));
            return false;
        }

        if ((settings.Write && settings.Check)
            || (settings.Convert && (settings.Write || settings.Check)))
        {
            report.Add(new Diagnostic(
                ConflictingModes,
                Severity.Error,
                "--write, --check and --convert ask for different things and cannot be combined.",
                "docs tasks",
                Hint: "Run --check to see what differs, --write to fix it, or --convert to replace a JSON file."));
            return false;
        }

        return true;
    }

    /// <summary>Prints the difficulty checks and the write/preview status after the report has been rendered.</summary>
    private void PrintOutcome(DocsTasksSettings settings, PlanTaskFileMode mode, PlanTaskFileOutcome outcome, DiagnosticReport report)
    {
        if (!settings.NoInfo && ReportRenderer.IsHuman(settings.FormatFor(console)))
        {
            foreach (string check in outcome.DifficultyChecks ?? [])
            {
                console.MarkupLine($"[grey]{Markup.Escape(check)}[/]");
            }
        }

        if (ReportRenderer.IsHuman(settings.FormatFor(console)) && !report.HasErrors && outcome.Json is not null)
        {
            if (mode == PlanTaskFileMode.Preview)
            {
                console.Profile.Out.Writer.Write(outcome.Json);
            }
            else if (mode is PlanTaskFileMode.Write or PlanTaskFileMode.Convert)
            {
                console.MarkupLine(
                    outcome.Wrote
                        ? $"[green]Wrote[/] {Markup.Escape(outcome.TaskFilePath)} ({outcome.TaskCount} task(s))."
                        : $"{Markup.Escape(outcome.TaskFilePath)} is already current ({outcome.TaskCount} task(s)); nothing written.");
            }
            else
            {
                console.MarkupLine($"{Markup.Escape(outcome.TaskFilePath)} is current ({outcome.TaskCount} task(s)).");
            }
        }
    }

    private async Task<IArbiterProvider?> DefaultPacketProviderAsync(
        KyberWeaveConfig config,
        CancellationToken cancellationToken)
    {
        if (config.Arbiter.Provider.Kind == ArbiterProviderKind.None)
        {
            return null;
        }

        string endpoint = config.Arbiter.Provider.Endpoint;
        bool loopback = Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;
        if (!loopback && await arbiterKeys.TryResolveKeyAsync(config, endpoint, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        return arbiterDryRun.CreateProvider(config, null, new DiagnosticReport(), out _);
    }

    private async Task CheckPacketsAsync(
        KyberWeaveConfig config, PlanTaskFile file, string plan, DiagnosticReport report, CancellationToken cancellationToken)
    {
        IArbiterProvider? provider = await PacketProviderAsync(config, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.PacketNotSelfContained,
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
