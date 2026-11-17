using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
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
internal sealed class SquadInstallCommand : Command<SquadInstallSettings>
{
    private readonly ISquadLifecycleService _lifecycle;
    private readonly ISquadStateStore _stateStore;
    private readonly ISquadStateMigration _migration;
    private readonly ISquadTargetResolver _targets;
    private readonly ISquadGlobalRootResolver _globalRoots;
    private readonly SquadConsoleOptions _console;
    private readonly KyberDashCapturePluginStep? _capturePluginStep;

    /// <summary>Creates the install command over the Squad services the host registers.</summary>
    /// <param name="lifecycle">Installs the deployment.</param>
    /// <param name="stateStore">Reads the deployment state.</param>
    /// <param name="migration">Moves legacy state into the home layout before the install reads it.</param>
    /// <param name="targets">Chooses the harness targets.</param>
    /// <param name="globalRoots">Names the roots a global install writes beneath.</param>
    /// <param name="console">How the command prompts; the defaults read the console.</param>
    /// <param name="capturePluginStep">Installs the capture plugin after a global deployment, or null for none.</param>
    public SquadInstallCommand(
        ISquadLifecycleService lifecycle,
        ISquadStateStore stateStore,
        ISquadStateMigration migration,
        ISquadTargetResolver targets,
        ISquadGlobalRootResolver globalRoots,
        SquadConsoleOptions console,
        KyberDashCapturePluginStep? capturePluginStep = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(globalRoots);
        ArgumentNullException.ThrowIfNull(console);
        _lifecycle = lifecycle;
        _stateStore = stateStore;
        _migration = migration;
        _targets = targets;
        _globalRoots = globalRoots;
        _console = console;
        _capturePluginStep = capturePluginStep;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadInstallSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ISquadStateStore stateStore = _stateStore;
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

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

        string projectRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);

        // Legacy state is migrated before the install reads any of it. A dry run reports what the next
        // real run would move, and stops without reading a deployment it has not yet moved.
        SquadMigrationReport migration = SquadCommandComposition.EnsureStateMigrated(
            _migration,
            Environment.CurrentDirectory,
            scope == SquadDeploymentScope.Project ? projectRoot : null,
            settings.DryRun);
        SquadCommandComposition.WriteMigrationNotes(migration);
        if (SquadCommandComposition.IsPendingDryRunMigration(migration, settings.DryRun))
        {
            return 0;
        }

        // A --global install is anchored at the home directory whatever the working directory is, or at the
        // recorded root of a legacy single-root receipt the migration has just copied. Chosen after the migration.
        string targetRoot = scope == SquadDeploymentScope.Global
            ? SquadCommandComposition.ResolveGlobalAnchorRoot(stateStore, effectivePath, settings.DryRun)
            : projectRoot;

        // Load configuration if present. A project that cannot be read — no config file,
        // or one that does not parse — still installs: the squad config falls back to product
        // defaults and the task-gate hint below says where review gates come from. Only a
        // global run, which has no project whose gates could be missing, keeps the load
        // failure fatal.
        KyberWeaveConfigLoadResult configResult = SquadCommandComposition.TryLoadScopeConfig(
            targetRoot,
            scope,
            stateStore.UserPaths.HomeDirectory);
        if (!configResult.Success && scope != SquadDeploymentScope.Project)
        {
            SquadCommandComposition.WriteClientInputError(configResult.Error ?? "Failed to load configuration.");
            return 1;
        }

        SquadConfig squadConfig = configResult.Config?.Squad ?? SquadConfig.ProductDefaults;

        // Perform target resolution
        SquadTargetResolutionRequest request = new()
        {
            RootPath = targetRoot,
            Operation = SquadTargetOperation.Install,
            ExplicitTargets = settings.Targets,
            ConfiguredTargets = squadConfig.Targets,
            ExplicitExclusions = settings.Exclusions,
            ConfiguredExclusions = squadConfig.Exclusions,
            IsInteractive = SquadCommandComposition.IsInteractiveConsole()
        };

        SquadTargetResolutionDecision decision = _targets.Resolve(request);
        if (decision.Kind == SquadTargetResolutionKind.Failure)
        {
            AnsiConsole.MarkupLine("[red]No deployment targets specified or detected.[/]");
            if (decision.RecoveryCommand is not null)
            {
                AnsiConsole.MarkupLine($"Specify target(s) using [bold]{Markup.Escape(decision.RecoveryCommand)}[/].");
            }

            return decision.ExitCode ?? 2;
        }

        // The host's Arbiter settings travel into rendering (Req 22.2, 5.3): enabled
        // verbatim, timeout-ms as whole hook seconds with headroom.
        SquadArbiterWiring arbiterWiring = SquadCommandComposition.ResolveArbiterWiring(
            configResult.Config?.Arbiter ?? Core.Arbiter.ArbiterConfig.ProductDefaults);
        // A --global run writes beneath each selected target's own physical global root,
        // not beneath the home anchor. The same injected resolver the lifecycle plans with
        // names the roots, so the confirmation shows the real destinations.
        ISquadLifecycleService lifecycleService = _lifecycle;

        SquadInstallRequest installRequest = new(
            targetRoot,
            scope,
            decision.Targets,
            settings.Exclusions,
            pinnedVersion,
            Adopt: settings.Adopt,
            DryRun: settings.DryRun,
            Arbiter: arbiterWiring,
            WorkerRoster: configResult.Config?.WorkerRoster);

        // A global install that names a target the install lacks adds it to the existing ones, so the confirmation
        // lists every root the run writes beneath, not only the new target's.
        IReadOnlyList<SquadTarget> deployedTargets = lifecycleService.EffectiveInstallTargets(installRequest);
        IReadOnlyList<(SquadTarget Target, string GlobalRoot)>? globalTargetRoots =
            scope == SquadDeploymentScope.Global
                ? SquadCommandComposition.ResolveGlobalTargetRoots(deployedTargets, _globalRoots)
                : null;

        // The confirmation is the last gate before the lifecycle call — the only
        // side-effecting step — so a decline aborts with zero writes (plan N3/N5);
        // a dry-run's output already names the root, so it never prompts (N4).
        if (!settings.DryRun && !SquadTargetRootConfirmation.Confirm(
                targetRoot,
                scope,
                "install",
                _console.IsInteractive ?? SquadCommandComposition.IsInteractiveConsole(),
                settings.Yes,
                _console.ReadAnswer ?? SquadTargetRootConfirmation.ReadConsoleAnswer,
                globalTargetRoots))
        {
            AnsiConsole.MarkupLine("[yellow]Declined. No changes were made.[/]");
            return 2;
        }

        try
        {
            SquadLifecycleResult result = lifecycleService.InstallAsync(installRequest, cancellationToken).GetAwaiter()
                .GetResult();
            if (result.Success)
            {
                if (settings.DryRun)
                {
                    int fileCount = result.Receipt?.Files.Count ?? 0;
                    SquadCommandComposition.WriteLine(
                        $"[bold]Dry-run:[/] planned {fileCount} deployed files for [bold]{Markup.Escape(targetRoot)}[/].");
                }
                else
                {
                    SquadCommandComposition.WriteLine(
                        $"[green]Successfully installed Kyber-Squad to [bold]{Markup.Escape(targetRoot)}[/].[/]");
                }

                if (deployedTargets.Count > decision.Targets.Count)
                {
                    SquadCommandComposition.WriteLine(
                        "Extended the existing global install; it now has "
                        + $"{Markup.Escape(string.Join(", ", deployedTargets.Select(SquadTargetCatalog.GetToken)))}.");
                }

                // Until trust is granted the hooks stay off (§10.7), so the install names
                // each selected target's trust gate where the audit cannot observe the gap.
                // Global Warp stays fallback-only: the install still prints the MCP registration
                // guidance, but it never mutates ~/.warp/.mcp.json.
                WriteArbiterTrustSteps(decision.Targets, arbiterWiring);
                if (scope == SquadDeploymentScope.Global && (decision.Targets.Contains(SquadTarget.Warp) || decision.Targets.Contains(SquadTarget.ZCode)))
                {
                    string targetToken = decision.Targets.Contains(SquadTarget.Warp) ? "warp" : "zcode";
                    string configPath = targetToken == "warp" ? "~/.warp/.mcp.json" : "~/.zcode/.mcp.json";
                    Console.WriteLine(RegistrationStepMessage(configPath));
                }

                SquadCommandComposition.WriteTransactionWarnings(result.Warnings);

                // A project with no task gates learns where they come from. Squad writes no
                // configuration itself, and the hint never changes this exit code.
                if (scope == SquadDeploymentScope.Project)
                {
                    SquadTaskGateHint.WriteIfNoTaskGates(targetRoot);
                }

                // The capture plugin is global, so only a global deployment reaches it. It runs after the
                // deployment has succeeded and cannot change this command's exit code.
                if (scope == SquadDeploymentScope.Global)
                    KyberDashCapturePluginStep.RunForGlobal(
                        _capturePluginStep,
                        decision.Targets,
                        settings.NoCapturePlugin,
                        settings.DryRun,
                        _globalRoots);
                return 0;
            }

            if (result.Errors is { Count: > 0 })
            {
                foreach (string error in result.Errors)
                {
                    SquadCommandComposition.WriteClientInputError(error);
                }
            }

            return 1;
        }
        catch (SquadDeploymentConflictException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 1;
        }
    }

    public int Execute(CommandContext context, SquadInstallSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    private static string RegistrationStepMessage(string configPath)
    {
        return $"Register kyber-weave-arbiter in {configPath}: kyber-weave-arbiter serve --repo-root <root>";
    }

    /// <summary>
    /// Prints the Arbiter trust gate for each selected target that has one.
    /// </summary>
    /// <remarks>
    /// Only when the Arbiter is enabled: a disabled wiring renders no hooks, so there is
    /// no gate to grant. Each line carries the target token and the step verbatim from
    /// <see cref="ArbiterHookWiring.TrustSteps"/> so the operator can copy it.
    /// </remarks>
    internal static void WriteArbiterTrustSteps(
        IReadOnlyList<SquadTarget> targets,
        SquadArbiterWiring wiring)
    {
        bool shouldEmitFallbackInstructions = targets.Contains(SquadTarget.Warp) || targets.Contains(SquadTarget.ZCode);
        if (!wiring.Enabled && !shouldEmitFallbackInstructions)
        {
            return;
        }

        // Unfolded: a redirected console folds at 80 columns, splitting the step
        // mid-phrase — corrupting the one thing the line exists for: a copy-pastable
        // gate the operator must grant where the audit cannot observe the gap.
        CommandHelpers.WriteUnfolded(() =>
        {
            foreach (SquadTarget target in targets)
            {
                if (ArbiterHookWiring.TrustSteps.TryGetValue(target, out string? step))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]Arbiter trust ({Markup.Escape(SquadTargetCatalog.GetToken(target))}):[/] {Markup.Escape(step)}");
                }
            }
        });
    }

}
