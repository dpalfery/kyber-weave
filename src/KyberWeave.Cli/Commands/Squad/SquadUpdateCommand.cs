using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Updates an existing Kyber-Squad deployment while preserving managed local edits.
/// </summary>
public sealed class SquadUpdateCommand : Command<SquadUpdateSettings>
{
    private readonly IProcessExecutor? _executor;
    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;
    private readonly ISquadReleaseSource? _releaseSource;
    private readonly ISquadRenderer? _renderer;

    /// <summary>Creates a new update command using default dependencies.</summary>
    public SquadUpdateCommand()
    {
    }

    /// <summary>Creates a new update command using injectable dependencies.</summary>
    internal SquadUpdateCommand(
        IProcessExecutor? executor = null,
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null)
    {
        _executor = executor;
        _userPaths = userPaths;
        _stateStore = stateStore;
        _releaseSource = releaseSource;
        _renderer = renderer;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadUpdateSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
        string targetRoot = SquadCommandComposition.ResolveTargetRoot(settings.Path);
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

        // Validate explicit targets and exclusions; invalid tokens return exit code 2
        try
        {
            if (settings.Targets.Length > 0)
                _ = SquadTargetCatalog.Parse(settings.Targets);

            if (settings.Exclusions.Length > 0)
                _ = SquadTargetCatalog.Parse(settings.Exclusions);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(ex.Message)}[/]");
            return 2;
        }

        SquadReceipt? receipt = stateStore.ReadReceipt(targetRoot, scope);
        if (receipt is null)
        {
            AnsiConsole.MarkupLine($"[red]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/].[/]");
            AnsiConsole.MarkupLine("Run [bold]kyber-weave squad install[/] to create an initial deployment.");
            return 1;
        }

        IReadOnlyList<SquadTarget> receiptTargets;
        try
        {
            receiptTargets = SquadTargetCatalog.Parse(receipt.Files.Select(f => f.Target).Distinct());
        }
        catch (ArgumentException)
        {
            receiptTargets = Array.Empty<SquadTarget>();
        }

        // Load configuration if present
        KyberWeaveConfigLoadResult configResult = KyberWeaveConfigLoader.TryLoad(targetRoot, null);
        if (!configResult.Success)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(configResult.Error ?? "Failed to load configuration.")}[/]");
            return 1;
        }

        SquadConfig squadConfig = configResult.Config?.Squad ?? SquadConfig.ProductDefaults;

        SquadTargetResolutionRequest request = new SquadTargetResolutionRequest
        {
            RootPath = targetRoot,
            Operation = SquadTargetOperation.Update,
            ExplicitTargets = settings.Targets,
            ConfiguredTargets = squadConfig.Targets,
            ReceiptTargets = receiptTargets,
            ExplicitExclusions = settings.Exclusions,
            ConfiguredExclusions = squadConfig.Exclusions,
            IsInteractive = SquadCommandComposition.IsInteractiveConsole()
        };

        SquadTargetResolutionDecision decision = SquadTargetResolver.Resolve(request);
        if (decision.Kind == SquadTargetResolutionKind.Failure)
        {
            AnsiConsole.MarkupLine("[red]No deployment targets specified or detected.[/]");
            if (decision.RecoveryCommand is not null)
            {
                AnsiConsole.MarkupLine($"Specify target(s) using [bold]{Markup.Escape(decision.RecoveryCommand)}[/].");
            }

            return decision.ExitCode ?? 2;
        }

        SquadLifecycleService lifecycleService = SquadCommandComposition.CreateLifecycleService(
            executor: _executor,
            userPaths: _userPaths,
            stateStore: stateStore,
            releaseSource: _releaseSource,
            renderer: _renderer);

        SquadUpdateRequest updateRequest = new(
            TargetRoot: targetRoot,
            Scope: scope,
            Targets: decision.Targets,
            Exclusions: settings.Exclusions,
            ReplaceManaged: settings.ReplaceManaged,
            DryRun: settings.DryRun);

        try
        {
            SquadLifecycleResult result = lifecycleService.UpdateAsync(updateRequest).GetAwaiter().GetResult();
            if (result.Success)
            {
                if (settings.DryRun)
                {
                    int fileCount = result.Receipt?.Files.Count ?? 0;
                    AnsiConsole.MarkupLine($"[bold]Dry-run:[/] planned {fileCount} deployed files for [bold]{Markup.Escape(targetRoot)}[/].");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[green]Successfully updated Kyber-Squad at [bold]{Markup.Escape(targetRoot)}[/].[/]");
                }

                return 0;
            }

            if (result.Errors is { Count: > 0 })
            {
                foreach (string error in result.Errors)
                {
                    AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(error)}[/]");
                }
            }

            return 1;
        }
        catch (SquadDeploymentConflictException ex)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
