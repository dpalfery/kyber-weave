using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Domain.StatusLine;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>
///     Stages the Kyber-owned status-line artifacts at per-user locations and prints the manual
///     activation snippet for each harness. It never writes or reads a harness settings file.
/// </summary>
/// <remarks>
///     Building a <see cref="StatusLineDeploymentPlan" /> is the dry run and the preflight at once: it
///     reads the receipt and the target paths and writes nothing, so <c>--dry-run</c> and the first half
///     of a real deploy are the same code path. Only <see cref="StatusLineDeploymentPlan.Apply" />
///     changes the filesystem.
/// </remarks>
public sealed class UtilitiesStatusLineDeployCommand : Command<UtilitiesStatusLineDeploySettings>
{
    private readonly IProcessEnvironment _environment;
    private readonly IStatusLineHostService _host;
    private readonly IStatusLineDeploymentService _plans;
    private readonly IFileSystemPathSemantics _semantics;
    private readonly IAnsiConsole _console;

    /// <summary>Creates a deploy command over the process environment and the host service.</summary>
    /// <param name="environment">The process state the per-user roots are read from.</param>
    /// <param name="host">Opens the artifact tree of the checkout and marks the deployed commands executable.</param>
    /// <param name="plans">Plans and applies the deployment.</param>
    /// <param name="semantics">The filesystem's containment rules.</param>
    /// <param name="console">Receives everything the operator reads.</param>
    public UtilitiesStatusLineDeployCommand(
        IProcessEnvironment environment,
        IStatusLineHostService host,
        IStatusLineDeploymentService plans,
        IFileSystemPathSemantics semantics,
        IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(console);

        _environment = environment;
        _host = host;
        _plans = plans;
        _semantics = semantics;
        _console = console;
    }

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        UtilitiesStatusLineDeploySettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IReadOnlyList<StatusLineTarget> targets;
        try
        {
            targets = UtilitiesStatusLineTargetCatalog.ParseSelection(settings.Target);
        }
        catch (ArgumentException ex)
        {
            UtilitiesStatusLineCommandComposition.WriteClientInputError(_console, ex.Message);
            return 2;
        }

        StatusLineTargetRoots roots;
        try
        {
            roots = UtilitiesStatusLineCommandComposition.ResolveRoots(_environment);
            foreach (StatusLineTarget target in targets)
            {
                _ = roots.ResolveStagingRoot(target);
            }
        }
        catch (ArgumentException ex)
        {
            UtilitiesStatusLineCommandComposition.WriteClientInputError(_console, ex.Message);
            return 2;
        }

        IUtilitiesStatusLineArtifactSource artifacts;
        try
        {
            artifacts = _host.ResolveArtifactSource();
        }
        catch (DirectoryNotFoundException ex)
        {
            _console.MarkupLine(
                $"[red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        bool hasIssues = false;
        foreach (StatusLineTarget target in targets)
            if (!DeployOne(target, roots, artifacts, settings.DryRun))
                hasIssues = true;

        return hasIssues ? 1 : 0;
    }

    public int Execute(CommandContext context, UtilitiesStatusLineDeploySettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    private bool DeployOne(
        StatusLineTarget target,
        StatusLineTargetRoots roots,
        IUtilitiesStatusLineArtifactSource artifacts,
        bool dryRun)
    {
        string token = UtilitiesStatusLineTargetCatalog.GetToken(target);

        IReadOnlyList<StatusLineDeploymentFile> files;
        StatusLineDeploymentPlan plan;
        try
        {
            files = artifacts.Load(target);
            plan = _plans.CreateDeploy(target, roots, files);
        }
        catch (Exception ex) when (
            ex is StatusLineDeploymentConflictException or InvalidDataException or
                IOException or UnauthorizedAccessException or ArgumentException)
        {
            _console.MarkupLine(
                $"[red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
            return false;
        }

        _console.MarkupLine($"[bold]{Markup.Escape(token)}[/]");

        if (dryRun)
        {
            _console.MarkupLine(
                $"  [grey]dry-run[/] would stage {plan.PlannedFileChanges.Count} file(s) beneath " +
                $"{Markup.Escape(plan.StagingRoot)}");
        }
        else
        {
            try
            {
                _plans.Apply(plan);
                _host.ApplyExecutableBit(plan.StagingRoot, files);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _console.MarkupLine(
                    $"[red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
                return false;
            }

            _console.MarkupLine(
                $"  [green]ok[/] staged {plan.PlannedFileChanges.Count} file(s) beneath " +
                $"{Markup.Escape(plan.StagingRoot)}");
        }

        string? commandPath;
        try
        {
            commandPath = UtilitiesStatusLineOutput.ResolvePrimaryCommandPath(
                target,
                plan.StagingRoot,
                files,
                _semantics);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _console.MarkupLine(
                $"[red]kyber-weave utilities statusline: error:[/] {Markup.Escape(ex.Message)}");
            return false;
        }

        if (commandPath is null)
        {
            _console.MarkupLine(
                $"  [yellow]warn[/] the {Markup.Escape(token)} artifact set has no " +
                $"'{Markup.Escape(UtilitiesStatusLineArtifacts.PrimaryFileName(target))}' to activate.");
            return true;
        }

        UtilitiesStatusLineOutput.PrintActivationGuidance(_console, target, commandPath);
        return true;
    }
}
