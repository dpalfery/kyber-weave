using KyberWeave.Arbiter.Hooks;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;
using KyberWeave.Domain.Update;

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
    private readonly SquadConsole _output;
    private readonly SquadTargetRootConfirmation _confirmation;
    private readonly IProcessEnvironment _environment;
    private readonly SquadCommandRoots _roots;
    private readonly KyberDashCapturePluginStep? _capturePluginStep;
    private readonly IKyberWeaveConfigRepository _configs;

    /// <summary>Creates the install command over the Squad services the host registers.</summary>
    /// <param name="lifecycle">Installs the deployment.</param>
    /// <param name="stateStore">Reads the deployment state.</param>
    /// <param name="migration">Moves legacy state into the home layout before the install reads it.</param>
    /// <param name="targets">Chooses the harness targets.</param>
    /// <param name="globalRoots">Names the roots a global install writes beneath.</param>
    /// <param name="console">How the command prompts; the defaults read the console.</param>
    /// <param name="output">Where the command writes and asks.</param>
    /// <param name="confirmation">Echoes the target root and asks before anything is written.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    /// <param name="roots">Resolves the target root, anchors a global run and reads the scope's configuration.</param>
    /// <param name="capturePluginStep">Installs the capture plugin after a global deployment, or null for none.</param>
    /// <param name="configs">Reads the project's host config for the task-gate hint.</param>
    public SquadInstallCommand(
        ISquadLifecycleService lifecycle,
        ISquadStateStore stateStore,
        ISquadStateMigration migration,
        ISquadTargetResolver targets,
        ISquadGlobalRootResolver globalRoots,
        SquadConsoleOptions console,
        SquadConsole output,
        SquadTargetRootConfirmation confirmation,
        IProcessEnvironment environment,
        SquadCommandRoots roots,
        IKyberWeaveConfigRepository configs,
        KyberDashCapturePluginStep? capturePluginStep = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(globalRoots);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(configs);
        _lifecycle = lifecycle;
        _stateStore = stateStore;
        _migration = migration;
        _targets = targets;
        _globalRoots = globalRoots;
        _console = console;
        _output = output;
        _confirmation = confirmation;
        _environment = environment;
        _roots = roots;
        _configs = configs;
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
        if (!TryResolveClientInput(settings, out string? effectivePath, out string? pinnedVersion))
        {
            return 2;
        }

        string projectRoot = _roots.ResolveTargetRoot(effectivePath);

        // Legacy state is migrated before the install reads any of it. A dry run reports what the next
        // real run would move, and stops without reading a deployment it has not yet moved.
        SquadMigrationReport migration = _roots.EnsureStateMigrated(
            _migration,
            _environment.CurrentDirectory,
            scope == SquadDeploymentScope.Project ? projectRoot : null,
            settings.DryRun);
        _output.WriteMigrationNotes(migration);
        if (SquadCommandComposition.IsPendingDryRunMigration(migration, settings.DryRun))
        {
            return 0;
        }

        // A --global install is anchored at the home directory whatever the working directory is, or at the
        // recorded root of a legacy single-root receipt the migration has just copied. Chosen after the migration.
        string targetRoot = scope == SquadDeploymentScope.Global
            ? _roots.ResolveGlobalAnchorRoot(stateStore, effectivePath, settings.DryRun)
            : projectRoot;

        SquadTargetResolutionDecision? resolved = PrepareDecision(
            targetRoot,
            scope,
            stateStore,
            settings,
            out KyberWeaveConfig? config,
            out int resolutionExitCode);
        if (resolved is null)
        {
            return resolutionExitCode;
        }

        SquadTargetResolutionDecision decision = resolved;

        // The host's Arbiter settings travel into rendering (Req 22.2, 5.3): enabled
        // verbatim, timeout-ms as whole hook seconds with headroom.
        SquadArbiterWiring arbiterWiring = SquadCommandComposition.ResolveArbiterWiring(
            config?.Arbiter ?? Core.Arbiter.ArbiterConfigDefaults.Product);
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
            WorkerRoster: config?.WorkerRoster);

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
        if (!settings.DryRun && !_confirmation.Confirm(
                targetRoot,
                scope,
                "install",
                _console.IsInteractive ?? _output.IsInteractive,
                settings.Yes,
                _console.ReadAnswer,
                globalTargetRoots))
        {
            _output.Ansi.MarkupLine("[yellow]Declined. No changes were made.[/]");
            return 2;
        }

        try
        {
            SquadLifecycleResult result = lifecycleService.InstallAsync(installRequest, cancellationToken).GetAwaiter()
                .GetResult();
            if (result.Success)
            {
                WriteInstallSuccess(result, settings, scope, targetRoot, decision, deployedTargets, arbiterWiring);
                return 0;
            }

            if (result.Errors is { Count: > 0 })
            {
                foreach (string error in result.Errors)
                {
                    _output.WriteClientInputError(error);
                }
            }

            return 1;
        }
        catch (SquadDeploymentConflictException ex)
        {
            _output.WriteFailure(ex);
            return 1;
        }
        // Documented boundary: an install failure is the command's ordinary error output, not an unhandled exception.
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            _output.WriteFailure(ex);
            return 1;
        }
    }

    public int Execute(CommandContext context, SquadInstallSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>
    ///     Loads configuration if present and resolves the install targets, or returns null with the exit code the
    ///     refusal has already been reported under.
    /// </summary>
    /// <remarks>
    ///     A project that cannot be read — no config file, or one that does not parse — still installs: the squad
    ///     config falls back to product defaults and the task-gate hint says where review gates come from. Only a
    ///     global run, which has no project whose gates could be missing, keeps the load failure fatal.
    /// </remarks>
    private SquadTargetResolutionDecision? PrepareDecision(
        string targetRoot,
        SquadDeploymentScope scope,
        ISquadStateStore stateStore,
        SquadInstallSettings settings,
        out KyberWeaveConfig? config,
        out int exitCode)
    {
        exitCode = 1;

        KyberWeaveConfigLoadResult configResult = _roots.TryLoadScopeConfig(
            _configs,
            targetRoot,
            scope,
            stateStore.UserPaths.HomeDirectory);
        if (!configResult.Success && scope != SquadDeploymentScope.Project)
        {
            _output.WriteClientInputError(configResult.Error ?? "Failed to load configuration.");
            config = null;
            return null;
        }

        config = configResult.Config;

        SquadConfig squadConfig = configResult.Config?.Squad ?? SquadConfig.ProductDefaults;

        SquadTargetResolutionRequest request = new()
        {
            RootPath = targetRoot,
            Operation = SquadTargetOperation.Install,
            ExplicitTargets = settings.Targets,
            ConfiguredTargets = squadConfig.Targets,
            ExplicitExclusions = settings.Exclusions,
            ConfiguredExclusions = squadConfig.Exclusions,
            IsInteractive = _output.IsInteractive
        };

        SquadTargetResolutionDecision decision = _targets.Resolve(request);
        if (decision.Kind == SquadTargetResolutionKind.Failure)
        {
            WriteResolutionFailure(decision);
            exitCode = decision.ExitCode ?? 2;
            return null;
        }

        exitCode = 0;
        return decision;
    }

    private bool TryResolveClientInput(
        SquadInstallSettings settings,
        out string? effectivePath,
        out string? pinnedVersion)
    {
        try
        {
            effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);

            if (settings.Targets.Length > 0)
                _ = SquadTargetCatalog.Parse(settings.Targets);

            if (settings.Exclusions.Length > 0)
                _ = SquadTargetCatalog.Parse(settings.Exclusions);

            pinnedVersion = SquadCommandComposition.NormalizePinnedVersion(settings.Version);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or SelfUpdateException)
        {
            _output.WriteClientInputError(ex.Message);
            effectivePath = null;
            pinnedVersion = null;
            return false;
        }
    }

    private void WriteResolutionFailure(SquadTargetResolutionDecision decision)
    {
        _output.Ansi.MarkupLine("[red]No deployment targets specified or detected.[/]");
        if (decision.RecoveryCommand is not null)
        {
            _output.Ansi.MarkupLine($"Specify target(s) using [bold]{Markup.Escape(decision.RecoveryCommand)}[/].");
        }
    }

    private void WriteInstallSuccess(
        SquadLifecycleResult result,
        SquadInstallSettings settings,
        SquadDeploymentScope scope,
        string targetRoot,
        SquadTargetResolutionDecision decision,
        IReadOnlyList<SquadTarget> deployedTargets,
        SquadArbiterWiring arbiterWiring)
    {
        if (settings.DryRun)
        {
            int fileCount = result.Receipt?.Files.Count ?? 0;
            _output.WriteLine(
                $"[bold]Dry-run:[/] planned {fileCount} deployed files for [bold]{Markup.Escape(targetRoot)}[/].");
        }
        else
        {
            _output.WriteLine(
                $"[green]Successfully installed Kyber-Squad to [bold]{Markup.Escape(targetRoot)}[/].[/]");
        }

        if (deployedTargets.Count > decision.Targets.Count)
        {
            _output.WriteLine(
                "Extended the existing global install; it now has "
                + $"{Markup.Escape(string.Join(", ", deployedTargets.Select(SquadTargetCatalog.GetToken)))}.");
        }

        // Until trust is granted the hooks stay off (§10.7), so the install names
        // each selected target's trust gate where the audit cannot observe the gap.
        // Global Warp stays fallback-only: the install still prints the MCP registration
        // guidance, but it never mutates ~/.warp/.mcp.json.
        WriteArbiterTrustSteps(_output, decision.Targets, arbiterWiring);
        if (arbiterWiring.Enabled && scope == SquadDeploymentScope.Project && decision.Targets.Contains(SquadTarget.OpenCode)
            && OpenCodeArbiterRegistrationStep(scope) is { } openCodeStep)
        {
            _output.Out.WriteLine(openCodeStep);
        }

        if (scope == SquadDeploymentScope.Global && (decision.Targets.Contains(SquadTarget.Warp) || decision.Targets.Contains(SquadTarget.ZCode)))
        {
            string targetToken = decision.Targets.Contains(SquadTarget.Warp)
                ? HarnessRegistry.WarpToken
                : HarnessRegistry.ZCodeToken;
            string configPath = targetToken == HarnessRegistry.WarpToken
                ? "~/.warp/.mcp.json"
                : "~/.zcode/.mcp.json";
            _output.Out.WriteLine(RegistrationStepMessage(configPath));
        }

        _output.WriteTransactionWarnings(result.Warnings);

        // A project with no task gates learns where they come from. Squad writes no
        // configuration itself, and the hint never changes this exit code.
        if (scope == SquadDeploymentScope.Project)
        {
            SquadTaskGateHint.WriteIfNoTaskGates(targetRoot, _configs, _output);
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
    }

    private static string RegistrationStepMessage(string configPath)
    {
        return $"Register kyber-weave-arbiter in {configPath}: kyber-weave-arbiter serve --repo-root <root>";
    }

    /// <summary>
    /// The OpenCode step that makes the Arbiter's MCP tools reachable, or null when the render cannot grant them.
    /// OpenCode takes its servers from <c>opencode.json</c>, which this deployment does not own; without the entry
    /// the conductor has no <c>arbiter_evaluate_agent_dispatch_or_return</c>, and the plugin hook remains the only
    /// gate. The grant is project-scope only (as on ZCode), so a global install prints nothing: its agents keep
    /// the hook path and would hold no permission for the server.
    /// </summary>
    internal static string? OpenCodeArbiterRegistrationStep(SquadDeploymentScope scope) =>
        scope != SquadDeploymentScope.Project
            ? null
            : "Register kw-arbiter in opencode.json: kyber-weave-arbiter serve --repo-root . — its MCP tools " +
              "(arbiter_evaluate_agent_dispatch_or_return) are served from it; the plugin hook still gates dispatch without it.";

    /// <summary>
    /// Prints the Arbiter trust gate for each selected target that has one.
    /// </summary>
    /// <remarks>
    /// Only when the Arbiter is enabled: a disabled wiring renders no hooks, so there is
    /// no gate to grant. Each line carries the target token and the step verbatim from
    /// <see cref="ArbiterHookWiring.TrustSteps"/> so the operator can copy it.
    /// </remarks>
    internal static void WriteArbiterTrustSteps(
        SquadConsole output,
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
        output.WriteUnfolded(() =>
        {
            foreach (SquadTarget target in targets)
            {
                if (ArbiterHookWiring.TrustSteps.TryGetValue(target, out string? step))
                {
                    output.Ansi.MarkupLine(
                        $"[yellow]Arbiter trust ({Markup.Escape(SquadTargetCatalog.GetToken(target))}):[/] {Markup.Escape(step)}");
                }
            }
        });
    }

}
