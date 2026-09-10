using KyberWeave.Core.Configuration;
using System.Threading;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Deploys canonical agents and skills to coding harness directories.
/// </summary>
public sealed class SquadInstallCommand : Command<SquadInstallSettings>
{
    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;
    private readonly ISquadReleaseSource? _releaseSource;
    private readonly ISquadRenderer? _renderer;

    /// <summary>Creates a new install command using default dependencies.</summary>
    public SquadInstallCommand()
    {
    }

    /// <summary>Creates a new install command using injectable dependencies.</summary>
    internal SquadInstallCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _releaseSource = releaseSource;
        _renderer = renderer;
    }

    /// <inheritdoc />
    protected override int Execute(CommandContext context, SquadInstallSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
        string targetRoot = SquadCommandComposition.ResolveTargetRoot(settings.Path);

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

        // Load configuration if present
        KyberWeaveConfigLoadResult configResult = KyberWeaveConfigLoader.TryLoad(targetRoot);
        if (!configResult.Success)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(configResult.Error ?? "Failed to load configuration.")}[/]");
            return 1;
        }

        SquadConfig squadConfig = configResult.Config?.Squad ?? SquadConfig.ProductDefaults;

        // Perform target resolution
        SquadTargetResolutionRequest request = new SquadTargetResolutionRequest
        {
            RootPath = targetRoot,
            Operation = SquadTargetOperation.Install,
            ExplicitTargets = settings.Targets,
            ConfiguredTargets = squadConfig.Targets,
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

        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);
        SquadLifecycleService lifecycleService = SquadCommandComposition.CreateLifecycleService(
            userPaths: _userPaths,
            stateStore: stateStore,
            releaseSource: _releaseSource,
            renderer: _renderer);

        SquadInstallRequest installRequest = new(
            TargetRoot: targetRoot,
            Scope: scope,
            Targets: decision.Targets,
            Exclusions: settings.Exclusions,
            Adopt: settings.Adopt,
            DryRun: settings.DryRun);

        try
        {
            SquadLifecycleResult result = lifecycleService.InstallAsync(installRequest, cancellationToken).GetAwaiter().GetResult();
            if (result.Success)
            {
                if (settings.DryRun)
                {
                    int fileCount = result.Receipt?.Files.Count ?? 0;
                    AnsiConsole.MarkupLine($"[bold]Dry-run:[/] planned {fileCount} deployed files for [bold]{Markup.Escape(targetRoot)}[/].");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[green]Successfully installed Kyber-Squad to [bold]{Markup.Escape(targetRoot)}[/].[/]");
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

    public int Execute(CommandContext context, SquadInstallSettings settings) => Execute(context, settings, CancellationToken.None);
}
