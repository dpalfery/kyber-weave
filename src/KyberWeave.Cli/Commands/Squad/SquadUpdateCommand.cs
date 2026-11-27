using KyberWeave.Core.FileSystem;
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
///     Updates an existing Kyber-Squad deployment while preserving managed local edits. A <c>--global</c>
///     update refreshes the one home-level global deployment and then, when asked, the registered projects.
/// </summary>
/// <remarks>
///     The project update is one method, <see cref="RunUpdate" />, shared by a plain project update and by each
///     project a global run selects. A selected project keeps its own configuration and receipt targets, and
///     receives only the version and the dry-run flag from the global run: the global run's <c>-t</c>, <c>-x</c>
///     and <c>--replace-managed</c> never apply to a project. Selection is the operator's consent, so a selected
///     project is not confirmed a second time.
/// </remarks>
internal sealed class SquadUpdateCommand : Command<SquadUpdateSettings>
{
    private readonly ISquadLifecycleService _lifecycle;
    private readonly ISquadInstanceService _instances;
    private readonly ISquadStateStore _stateStore;
    private readonly ISquadStateMigration _migration;
    private readonly ISquadTargetResolver _targets;
    private readonly ISquadGlobalRootResolver _globalRoots;
    private readonly ISquadFileAccess _files;
    private readonly ISquadProjectPicker? _picker;
    private readonly SquadConsoleOptions _console;
    private readonly SquadConsole _output;
    private readonly SquadTargetRootConfirmation _confirmation;
    private readonly IProcessEnvironment _environment;
    private readonly SquadCommandRoots _roots;
    private readonly KyberDashCapturePluginStep? _capturePluginStep;
    private readonly IKyberWeaveConfigRepository _configs;

    /// <summary>Creates the update command over the Squad services the host registers.</summary>
    /// <param name="lifecycle">Updates the deployment.</param>
    /// <param name="instances">Refreshes the harness instances after a global update.</param>
    /// <param name="stateStore">Reads the deployment state.</param>
    /// <param name="migration">Moves legacy state into the home layout before the update reads it.</param>
    /// <param name="targets">Chooses the harness targets.</param>
    /// <param name="globalRoots">Names the roots a global update writes beneath.</param>
    /// <param name="files">Lists the installed instances.</param>
    /// <param name="console">How the command prompts; the defaults read the console.</param>
    /// <param name="output">Where the command writes and asks.</param>
    /// <param name="confirmation">Echoes the target root and asks before anything is written.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    /// <param name="roots">Resolves the target root, anchors a global run and reads the scope's configuration.</param>
    /// <param name="picker">Offers the registered projects to choose from, or null for the terminal picker.</param>
    /// <param name="capturePluginStep">Installs the capture plugin after a global deployment, or null for none.</param>
    /// <param name="configs">Reads the project's host config for the task-gate hint.</param>
    public SquadUpdateCommand(
        ISquadLifecycleService lifecycle,
        ISquadInstanceService instances,
        ISquadStateStore stateStore,
        ISquadStateMigration migration,
        ISquadTargetResolver targets,
        ISquadGlobalRootResolver globalRoots,
        ISquadFileAccess files,
        SquadConsoleOptions console,
        SquadConsole output,
        SquadTargetRootConfirmation confirmation,
        IProcessEnvironment environment,
        SquadCommandRoots roots,
        IKyberWeaveConfigRepository configs,
        ISquadProjectPicker? picker = null,
        KyberDashCapturePluginStep? capturePluginStep = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(globalRoots);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(configs);
        _lifecycle = lifecycle;
        _instances = instances;
        _stateStore = stateStore;
        _migration = migration;
        _targets = targets;
        _globalRoots = globalRoots;
        _files = files;
        _console = console;
        _output = output;
        _confirmation = confirmation;
        _environment = environment;
        _roots = roots;
        _configs = configs;
        _picker = picker;
        _capturePluginStep = capturePluginStep;
    }

    /// <summary>
    ///     Validates that each deployed instance with a receipt has its corresponding model preset file on disk.
    /// </summary>
    /// <param name="userPaths">The user paths provider.</param>
    /// <returns><c>true</c> if all deployed instance presets exist; otherwise, <c>false</c>.</returns>
    private bool ValidateInstancePresets(ISquadUserPaths userPaths)
    {
        string home = userPaths.HomeDirectory;
        string instanceStateBase = UserStatePaths.SquadInstanceStateDirectory(home);
        if (!_files.DirectoryExists(instanceStateBase)) return true;

        string presetsDir = UserStatePaths.SquadPresetsDirectory(home);

        foreach (SquadDirectoryEntry targetDir in _files.EnumerateRealDirectories(instanceStateBase))
        {
            SquadTarget? matchedTarget = null;
            foreach (SquadTarget candidate in SquadTargetCatalog.All)
            {
                if (string.Equals(SquadTargetCatalog.GetToken(candidate), targetDir.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchedTarget = candidate;
                    break;
                }
            }

            if (matchedTarget is null) continue;

            foreach (SquadDirectoryEntry presetDir in _files.EnumerateRealDirectories(targetDir.FullPath))
            {
                string receiptFile = Path.Combine(presetDir.FullPath, "squad.receipt.json");
                if (!_files.FileExists(receiptFile)) continue;

                string presetName = presetDir.Name;
                string presetFile = Path.Combine(presetsDir, $"{presetName}.yml");
                if (!_files.FileExists(presetFile))
                {
                    _output.WriteLine(
                        $"[red]error:[/] Missing preset file '{Markup.Escape(presetFile)}' for instance '{Markup.Escape(targetDir.Name)}/{Markup.Escape(presetName)}'.");
                    return false;
                }
            }
        }

        return true;
    }


    public override int Execute(CommandContext context, SquadUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ISquadStateStore stateStore = _stateStore;
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

        // Coalesce the positional path with --path and validate explicit targets,
        // exclusions, the pinned version, and the project selection; invalid client input
        // returns exit code 2 before any resolution, migration, or network call
        string? effectivePath;
        string? pinnedVersion;
        SquadProjectSelection selection;
        try
        {
            effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);

            if (settings.Targets.Length > 0)
                _ = SquadTargetCatalog.Parse(settings.Targets);

            if (settings.Exclusions.Length > 0)
                _ = SquadTargetCatalog.Parse(settings.Exclusions);

            pinnedVersion = SquadCommandComposition.NormalizePinnedVersion(settings.Version);

            selection = SquadCommandComposition.ParseProjectSelection(settings.Projects);
            if (selection.Kind != SquadProjectSelectionKind.Absent && !settings.Global)
                throw new ArgumentException(
                    "--projects applies only to a --global update. Add --global to update registered projects, " +
                    "or drop --projects to update this project alone.");
        }
        catch (Exception ex) when (ex is ArgumentException or SelfUpdateException)
        {
            _output.WriteClientInputError(ex.Message);
            return 2;
        }

        string projectRoot = _roots.ResolveTargetRoot(effectivePath);

        // A named project that cannot be resolved is refused before any global work, so a typo costs
        // nothing: no migration, no receipt read, no write.
        IReadOnlyList<string> namedProjects = [];
        if (scope == SquadDeploymentScope.Global && selection.Kind == SquadProjectSelectionKind.Paths)
            try
            {
                namedProjects = _roots.ResolveNamedProjects(selection.Paths, stateStore);
            }
            catch (ArgumentException ex)
            {
                _output.WriteClientInputError(ex.Message);
                return 2;
            }
            catch (InvalidDataException ex)
            {
                _output.WriteClientInputError(ex.Message);
                return 1;
            }

        // State is migrated before anything reads it. A dry run that finds legacy state reports it and
        // stops without reading a deployment the migration has not yet moved.
        SquadMigrationReport migration = _roots.EnsureStateMigrated(
            _migration,
            _environment.CurrentDirectory,
            scope == SquadDeploymentScope.Project ? projectRoot : null,
            settings.DryRun);
        _output.WriteMigrationNotes(migration);
        if (SquadCommandComposition.IsPendingDryRunMigration(migration, settings.DryRun)) return 0;

        // Chosen after the migration, so a legacy receipt the migration has just copied is anchored on its recorded root.
        string targetRoot = scope == SquadDeploymentScope.Global
            ? _roots.ResolveGlobalAnchorRoot(stateStore, effectivePath, settings.DryRun)
            : projectRoot;

        ISquadLifecycleService lifecycleService = _lifecycle;

        bool interactive = _console.IsInteractive ?? _output.IsInteractive;

        UpdateRun run = new(
            targetRoot,
            scope,
            settings.Targets,
            settings.Exclusions,
            pinnedVersion,
            settings.ReplaceManaged,
            settings.DryRun,
            settings.Yes);
        if (scope == SquadDeploymentScope.Global)
        {
            SquadReceipt? globalReceipt = stateStore.ReadReceipt(targetRoot, scope);
            if (globalReceipt is not null)
            {
                List<SquadTarget> deployed = [];
                int exitCode = RunUpdate(run, stateStore, lifecycleService, interactive, cancellationToken, deployed);
                if (exitCode != 0) return exitCode;

                // Runs after the global deployment has updated and cannot change this command's exit code.
                KyberDashCapturePluginStep.RunForGlobal(
                    _capturePluginStep,
                    deployed,
                    settings.NoCapturePlugin,
                    settings.DryRun,
                    _globalRoots);
            }

            ISquadUserPaths paths = stateStore.UserPaths;
            if (!ValidateInstancePresets(paths))
            {
                return 1;
            }

            try
            {
                _instances.RefreshAllAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (SquadDeploymentConflictException ex)
            {
                _output.WriteLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
            catch (Exception ex)
            {
                _output.WriteClientInputError($"Instance refresh failed: {ex.Message}");
                return 1;
            }

            return UpdateSelectedProjects(
                settings,
                pinnedVersion,
                selection,
                namedProjects,
                stateStore,
                lifecycleService,
                interactive,
                cancellationToken);
        }

        return RunUpdate(run, stateStore, lifecycleService, interactive, cancellationToken);
    }

    /// <summary>
    ///     Updates the projects a global run selected: the named ones, every non-stale one for <c>all</c>, or
    ///     the ones the picker returns. Runs sequentially, continues past a failure, and prints a summary.
    /// </summary>
    /// <returns>Exit 1 when any selected project failed; otherwise 0.</returns>
    private int UpdateSelectedProjects(
        SquadUpdateSettings settings,
        string? pinnedVersion,
        SquadProjectSelection selection,
        IReadOnlyList<string> namedProjects,
        ISquadStateStore stateStore,
        ISquadLifecycleService lifecycleService,
        bool interactive,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SquadProjectRegistryEntry> registry = stateStore.ListProjects();
        IReadOnlyList<string> chosen = selection.Kind switch
        {
            SquadProjectSelectionKind.All => registry.Where(project => !project.Stale).Select(project => project.Path)
                .ToArray(),
            SquadProjectSelectionKind.Paths => namedProjects,
            SquadProjectSelectionKind.Absent when interactive && registry.Count > 0 =>
                SquadCommandComposition.RunProjectPicker(_picker, registry, _output.Ansi),
            _ => []
        };

        if (chosen.Count == 0) return 0;

        List<ProjectOutcome> outcomes = [];
        foreach (string path in chosen)
        {
            SquadProjectRegistryEntry? entry = registry.FirstOrDefault(project =>
                string.Equals(project.Path, path, StringComparison.Ordinal));
            if (entry is { Stale: true })
            {
                _output.WriteLine(
                    $"[yellow]warning:[/] skipping stale project '{Markup.Escape(path)}': " +
                    $"{Markup.Escape(entry.StaleReason ?? "it no longer carries a deployment")}.");
                outcomes.Add(new ProjectOutcome(path, ProjectState.Skipped));
                continue;
            }

            SquadMigrationReport import =
                SquadCommandComposition.ImportProjectIfHomeStateAbsent(stateStore, _migration, path, settings.DryRun);
            _output.WriteMigrationNotes(import);
            if (settings.DryRun && import.ImportedProjects.Count > 0)
            {
                // A dry run imports nothing, so the project has no home state yet and a plan against it would
                // describe a run the real update never makes. The next non-dry-run invocation imports and updates it.
                outcomes.Add(new ProjectOutcome(path, ProjectState.Skipped));
                continue;
            }

            UpdateRun projectRun = new(
                path,
                SquadDeploymentScope.Project,
                [],
                [],
                pinnedVersion,
                false,
                settings.DryRun,
                true);
            int code = RunUpdate(projectRun, stateStore, lifecycleService, interactive, cancellationToken);
            outcomes.Add(new ProjectOutcome(path, code == 0 ? ProjectState.Updated : ProjectState.Failed));
        }

        return WriteSummary(outcomes);
    }

    /// <summary>Prints the per-project summary, one unfolded line per project, and returns the run's exit code.</summary>
    private int WriteSummary(IReadOnlyList<ProjectOutcome> outcomes)
    {
        int updated = outcomes.Count(outcome => outcome.State == ProjectState.Updated);
        int failed = outcomes.Count(outcome => outcome.State == ProjectState.Failed);
        int skipped = outcomes.Count(outcome => outcome.State == ProjectState.Skipped);

        _output.WriteLine(
            $"[bold]Project update summary:[/] {updated} updated, {failed} failed, {skipped} skipped.");
        foreach (ProjectOutcome outcome in outcomes)
        {
            string label = outcome.State switch
            {
                ProjectState.Updated => "[green]updated[/]",
                ProjectState.Failed => "[red]failed[/]",
                _ => "[yellow]skipped[/]"
            };
            _output.WriteLine($"  {label} {Markup.Escape(outcome.Path)}");
        }

        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    ///     Updates one deployment: a global one, a project's own, or a project a global run selected. This is the
    ///     single project update path, so every form gets the same planning, confirmation and transaction.
    /// </summary>
    /// <param name="deployed">Receives the resolved targets when the update succeeds, so a global caller can act on them.</param>
    /// <returns>0 on success, 2 when the operator declined or the resolution refused, and 1 on failure.</returns>
    private int RunUpdate(
        UpdateRun run,
        ISquadStateStore stateStore,
        ISquadLifecycleService lifecycleService,
        bool interactive,
        CancellationToken cancellationToken,
        List<SquadTarget>? deployed = null)
    {
        SquadReceipt? receipt = stateStore.ReadReceipt(run.TargetRoot, run.Scope);
        if (receipt is null)
        {
            _output.WriteLine(
                $"[red]No Kyber-Squad deployment found at [bold]{Markup.Escape(run.TargetRoot)}[/].[/]");
            _output.WriteLine(
                "Run [bold]kyber-weave squad install[/] to create an initial deployment.");
            return 1;
        }

        IReadOnlyList<SquadTarget> receiptTargets;
        try
        {
            receiptTargets = SquadTargetCatalog.Parse(receipt.Files.Select(f => f.Target).Distinct());
        }
        catch (ArgumentException)
        {
            receiptTargets = [];
        }

        UpdateTargets? targets = ResolveUpdateTargets(run, receiptTargets, stateStore, out int resolveExitCode);
        if (targets is null)
        {
            return resolveExitCode;
        }

        SquadTargetResolutionDecision decision = targets.Decision;
        SquadArbiterWiring arbiterWiring = targets.Arbiter;

        SquadUpdateRequest updateRequest = new(
            run.TargetRoot,
            run.Scope,
            decision.Targets,
            run.ExplicitExclusions,
            run.Version,
            ReplaceManaged: run.ReplaceManaged,
            DryRun: run.DryRun,
            Arbiter: arbiterWiring,
            WorkerRoster: targets.WorkerRoster);

        // A --global run writes beneath each selected target's own physical global root,
        // not beneath the home anchor. Resolve the roots through the same resolver the
        // lifecycle plans with, so the confirmation names the real destinations.
        IReadOnlyList<(SquadTarget Target, string GlobalRoot)>? globalTargetRoots =
            run.Scope == SquadDeploymentScope.Global
                ? SquadCommandComposition.ResolveGlobalTargetRoots(decision.Targets, _globalRoots)
                : null;

        // The confirmation is the last gate before the lifecycle call — the only
        // side-effecting step — so a decline aborts with zero writes (plan N3/N5);
        // a dry-run's output already names the root, so it never prompts (N4).
        if (!run.DryRun && !_confirmation.Confirm(
                run.TargetRoot,
                run.Scope,
                "update",
                interactive,
                run.Yes,
                _console.ReadAnswer,
                globalTargetRoots))
        {
            _output.Ansi.MarkupLine("[yellow]Declined. No changes were made.[/]");
            return 2;
        }

        try
        {
            SquadLifecycleResult result = lifecycleService.UpdateAsync(updateRequest, cancellationToken).GetAwaiter()
                .GetResult();
            if (result.Success)
            {
                WriteUpdateSuccess(result, run, decision, arbiterWiring, deployed);
                return 0;
            }

            if (result.Errors is { Count: > 0 })
                foreach (string error in result.Errors)
                    _output.WriteClientInputError(error);

            return 1;
        }
        catch (SquadDeploymentConflictException ex)
        {
            _output.WriteFailure(ex);
            return 1;
        }
        catch (Exception ex)
        {
            _output.WriteFailure(ex);
            return 1;
        }
    }

    /// <summary>
    ///     Loads the scope's configuration and resolves the targets the update writes, or returns null with the exit
    ///     code the refusal has already been reported under.
    /// </summary>
    private UpdateTargets? ResolveUpdateTargets(
        UpdateRun run,
        IReadOnlyList<SquadTarget> receiptTargets,
        ISquadStateStore stateStore,
        out int exitCode)
    {
        exitCode = 1;

        KyberWeaveConfigLoadResult configResult = _roots.TryLoadScopeConfig(
            _configs,
            run.TargetRoot,
            run.Scope,
            stateStore.UserPaths.HomeDirectory);
        if (!configResult.Success)
        {
            _output.WriteClientInputError(configResult.Error ?? "Failed to load configuration.");
            return null;
        }

        SquadConfig squadConfig = configResult.Config?.Squad ?? SquadConfig.ProductDefaults;

        SquadTargetResolutionRequest request = new()
        {
            RootPath = run.TargetRoot,
            Operation = SquadTargetOperation.Update,
            ExplicitTargets = run.ExplicitTargets,
            ConfiguredTargets = squadConfig.Targets,
            ReceiptTargets = receiptTargets,
            ExplicitExclusions = run.ExplicitExclusions,
            ConfiguredExclusions = squadConfig.Exclusions,
            IsInteractive = _output.IsInteractive
        };

        SquadTargetResolutionDecision decision = _targets.Resolve(request);
        if (decision.Kind == SquadTargetResolutionKind.Failure)
        {
            _output.Ansi.MarkupLine("[red]No deployment targets specified or detected.[/]");
            if (decision.RecoveryCommand is not null)
                _output.Ansi.MarkupLine($"Specify target(s) using [bold]{Markup.Escape(decision.RecoveryCommand)}[/].");

            exitCode = decision.ExitCode ?? 2;
            return null;
        }

        // The host's Arbiter settings travel into rendering (Req 22.2, 5.3): enabled
        // verbatim, timeout-ms as whole hook seconds with headroom.
        SquadArbiterWiring arbiterWiring = SquadCommandComposition.ResolveArbiterWiring(
            configResult.Config?.Arbiter ?? Core.Arbiter.ArbiterConfigDefaults.Product);

        exitCode = 0;
        return new UpdateTargets(decision, arbiterWiring, configResult.Config?.WorkerRoster);
    }

    /// <summary>Prints what a successful update did and the steps the operator still has to take.</summary>
    private void WriteUpdateSuccess(
        SquadLifecycleResult result,
        UpdateRun run,
        SquadTargetResolutionDecision decision,
        SquadArbiterWiring arbiterWiring,
        List<SquadTarget>? deployed)
    {
        deployed?.AddRange(decision.Targets);
        if (run.DryRun)
        {
            int fileCount = result.Receipt?.Files.Count ?? 0;
            _output.WriteLine(
                $"[bold]Dry-run:[/] planned {fileCount} deployed files for [bold]{Markup.Escape(run.TargetRoot)}[/].");
        }
        else
        {
            _output.WriteLine(
                $"[green]Successfully updated Kyber-Squad at [bold]{Markup.Escape(run.TargetRoot)}[/].[/]");
        }

        _output.WriteTransactionWarnings(result.Warnings);

        // A project with no task gates learns where they come from. Squad writes no
        // configuration itself, and the hint never changes this exit code.
        if (run.Scope == SquadDeploymentScope.Project)
        {
            SquadTaskGateHint.WriteIfNoTaskGates(run.TargetRoot, _configs, _output);
        }

        // Until trust is granted the hooks stay off (§10.7), so the update names
        // each selected target's trust gate where the audit cannot observe the gap.
        SquadInstallCommand.WriteArbiterTrustSteps(_output, decision.Targets, arbiterWiring);
    }

    /// <summary>Runs the update without a cancellation token, for callers that have none to pass.</summary>
    public int Execute(CommandContext context, SquadUpdateSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>One deployment update: the root it writes, what it targets, and what consent it carries.</summary>
    /// <param name="TargetRoot">The root the update names: the home anchor for global, a project root otherwise.</param>
    /// <param name="Scope">Whether the update writes global or project state.</param>
    /// <param name="ExplicitTargets">Targets named on the command line; empty uses the receipt's own.</param>
    /// <param name="ExplicitExclusions">Targets excluded on the command line.</param>
    /// <param name="Version">The pinned release version, or null for the default.</param>
    /// <param name="ReplaceManaged">Whether locally edited owned files are overwritten.</param>
    /// <param name="DryRun">Whether the update only plans.</param>
    /// <param name="Yes">Whether the confirmation prompt is skipped.</param>
    private sealed record UpdateTargets(
        SquadTargetResolutionDecision Decision,
        SquadArbiterWiring Arbiter,
        string? WorkerRoster);

    private sealed record UpdateRun(
        string TargetRoot,
        SquadDeploymentScope Scope,
        string[] ExplicitTargets,
        string[] ExplicitExclusions,
        string? Version,
        bool ReplaceManaged,
        bool DryRun,
        bool Yes);

    private enum ProjectState
    {
        Updated,
        Failed,
        Skipped
    }

    private sealed record ProjectOutcome(string Path, ProjectState State);
}
