using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Configuration;
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
    private readonly bool? _isInteractive;
    private readonly Func<string, bool>? _readAnswer;

    /// <summary>Creates a new install command using default dependencies.</summary>
    public SquadInstallCommand()
    {
    }

    /// <summary>Creates a new install command using injectable dependencies.</summary>
    /// <remarks>
    /// <paramref name="isInteractive"/> and <paramref name="readAnswer"/> default to null
    /// because optional parameter values must be compile-time constants; Execute resolves
    /// them to <see cref="SquadCommandComposition.IsInteractiveConsole"/> and real console
    /// input, so an unparameterized run behaves exactly like the default collaborators.
    /// </remarks>
    internal SquadInstallCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _releaseSource = releaseSource;
        _renderer = renderer;
        _isInteractive = isInteractive;
        _readAnswer = readAnswer;
    }

    /// <inheritdoc />
    protected override int Execute(CommandContext context, SquadInstallSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);

        // Coalesce the positional path with --path and validate explicit targets,
        // exclusions, and the pinned version; invalid client input returns exit code 2
        // before any resolution or network call
        string? effectivePath;
        string? pinnedVersion;
        try
        {
            effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);

            if (settings.Targets.Length > 0)
                _ = SquadTargetCatalog.Parse(settings.Targets);

            if (settings.Exclusions.Length > 0)
                _ = SquadTargetCatalog.Parse(settings.Exclusions);

            pinnedVersion = SquadCommandComposition.NormalizePinnedVersion(settings.Version);
        }
        catch (Exception ex) when (ex is ArgumentException or SelfUpdateException)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        string targetRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);

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

        // A --global run writes beneath each selected target's own physical global root,
        // not beneath targetRoot — the state anchor. Resolve the roots the way the
        // lifecycle's plan will, so the confirmation names the real destinations.
        IReadOnlyList<(SquadTarget Target, string GlobalRoot)>? globalTargetRoots =
            scope == SquadDeploymentScope.Global
                ? SquadCommandComposition.ResolveGlobalTargetRoots(decision.Targets)
                : null;

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
            Version: pinnedVersion,
            Adopt: settings.Adopt,
            DryRun: settings.DryRun);

        // The confirmation is the last gate before the lifecycle call — the only
        // side-effecting step — so a decline aborts with zero writes (plan N3/N5);
        // a dry-run's output already names the root, so it never prompts (N4).
        if (!settings.DryRun && !SquadTargetRootConfirmation.Confirm(
                targetRoot,
                scope,
                "install",
                isInteractive: _isInteractive ?? SquadCommandComposition.IsInteractiveConsole(),
                yes: settings.Yes,
                readAnswer: _readAnswer ?? SquadTargetRootConfirmation.ReadConsoleAnswer,
                globalTargetRoots: globalTargetRoots))
        {
            AnsiConsole.MarkupLine("[yellow]Declined. No changes were made.[/]");
            return 2;
        }

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
