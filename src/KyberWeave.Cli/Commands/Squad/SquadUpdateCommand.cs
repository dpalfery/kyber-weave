using System.Text;
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
public sealed class SquadUpdateCommand : Command<SquadUpdateSettings>
{
    private readonly ISquadGlobalRootResolver? _globalRoots;
    private readonly SquadInstanceService? _instanceService;
    private readonly bool? _isInteractive;
    private readonly ISquadProjectPicker? _picker;
    private readonly Func<string, bool>? _readAnswer;
    private readonly ISquadReleaseSource? _releaseSource;
    private readonly ISquadRenderer? _renderer;
    private readonly SquadStateStore? _stateStore;
    private readonly ISquadUserPaths? _userPaths;

    /// <summary>Creates a new update command using default dependencies.</summary>
    public SquadUpdateCommand()
    {
    }

    /// <summary>Creates a new update command using injectable dependencies.</summary>
    /// <remarks>
    ///     <paramref name="isInteractive" /> and <paramref name="readAnswer" /> default to null
    ///     because optional parameter values must be compile-time constants; Execute resolves
    ///     them to <see cref="SquadCommandComposition.IsInteractiveConsole" /> and real console
    ///     input, so an unparameterized run behaves exactly like the default collaborators.
    /// </remarks>
    internal SquadUpdateCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null,
        ISquadGlobalRootResolver? globalRoots = null,
        ISquadProjectPicker? picker = null,
        SquadInstanceService? instanceService = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _releaseSource = releaseSource;
        _renderer = renderer;
        _isInteractive = isInteractive;
        _readAnswer = readAnswer;
        _globalRoots = globalRoots;
        _picker = picker;
        _instanceService = instanceService;
    }

    /// <inheritdoc />
    private static void EnsureInstancePresetsExist(ISquadUserPaths userPaths)
    {
        string home = userPaths.HomeDirectory;
        string instanceStateBase = Path.Combine(home, ".kyber-weave", "squad", "instance-state");
        if (!Directory.Exists(instanceStateBase)) return;

        string presetsDir = Path.Combine(home, ".kyber-weave", "squad", "presets");

        DirectoryInfo baseDir = new(instanceStateBase);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        foreach (FileSystemInfo targetEntry in baseDir.EnumerateFileSystemInfos("*", options))
        {
            if (targetEntry is not DirectoryInfo targetDir || targetEntry.LinkTarget is not null) continue;

            foreach (FileSystemInfo presetEntry in targetDir.EnumerateFileSystemInfos("*", options))
            {
                if (presetEntry is not DirectoryInfo presetDir || presetEntry.LinkTarget is not null) continue;

                string presetName = presetDir.Name;
                string presetFile = Path.Combine(presetsDir, $"{presetName}.yml");
                if (!File.Exists(presetFile))
                    try
                    {
                        Directory.CreateDirectory(presetsDir);
                        string stubPreset =
                            "schema: kyber-squad.model-preset/v1\nprofiles:\n  general:\n    default: default-model\n";
                        File.WriteAllText(presetFile, stubPreset, new UTF8Encoding(false));
                    }
                    catch (Exception)
                    {
                    }
            }
        }

        SquadCommandComposition.SyncPresetFiles(userPaths);
    }

    public override int Execute(CommandContext context, SquadUpdateSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);
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
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        string projectRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);

        // A named project that cannot be resolved is refused before any global work, so a typo costs
        // nothing: no migration, no receipt read, no write.
        IReadOnlyList<string> namedProjects = [];
        if (scope == SquadDeploymentScope.Global && selection.Kind == SquadProjectSelectionKind.Paths)
            try
            {
                namedProjects = SquadCommandComposition.ResolveNamedProjects(selection.Paths, stateStore);
            }
            catch (ArgumentException ex)
            {
                SquadCommandComposition.WriteClientInputError(ex.Message);
                return 2;
            }
            catch (InvalidDataException ex)
            {
                SquadCommandComposition.WriteClientInputError(ex.Message);
                return 1;
            }

        // State is migrated before anything reads it. A dry run that finds legacy state reports it and
        // stops without reading a deployment the migration has not yet moved.
        SquadMigrationReport migration = SquadCommandComposition.EnsureStateMigrated(
            stateStore,
            Environment.CurrentDirectory,
            scope == SquadDeploymentScope.Project ? projectRoot : null,
            settings.DryRun);
        SquadCommandComposition.WriteMigrationNotes(migration);
        if (SquadCommandComposition.IsPendingDryRunMigration(migration, settings.DryRun)) return 0;

        // Chosen after the migration, so a legacy receipt the migration has just copied is anchored on its recorded root.
        string targetRoot = scope == SquadDeploymentScope.Global
            ? SquadCommandComposition.ResolveGlobalAnchorRoot(stateStore, effectivePath, settings.DryRun)
            : projectRoot;

        SquadLifecycleService lifecycleService = SquadCommandComposition.CreateLifecycleService(
            _userPaths,
            stateStore,
            _releaseSource,
            _renderer,
            globalRoots: _globalRoots);

        bool interactive = _isInteractive ?? SquadCommandComposition.IsInteractiveConsole();

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
                int exitCode = RunUpdate(run, stateStore, lifecycleService, interactive, cancellationToken);
                if (exitCode != 0) return exitCode;
            }

            try
            {
                ISquadUserPaths paths = _userPaths ?? stateStore.UserPaths;
                EnsureInstancePresetsExist(paths);
                SquadInstanceService instanceService = _instanceService ??
                                                       SquadCommandComposition.CreateInstanceService(paths);
                instanceService.RefreshAllAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                SquadCommandComposition.WriteClientInputError($"Instance refresh failed: {ex.Message}");
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
        SquadStateStore stateStore,
        SquadLifecycleService lifecycleService,
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
                SquadCommandComposition.RunProjectPicker(_picker, registry),
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
                SquadCommandComposition.WriteLine(
                    $"[yellow]warning:[/] skipping stale project '{Markup.Escape(path)}': " +
                    $"{Markup.Escape(entry.StaleReason ?? "it no longer carries a deployment")}.");
                outcomes.Add(new ProjectOutcome(path, ProjectState.Skipped));
                continue;
            }

            SquadMigrationReport import =
                SquadCommandComposition.ImportProjectIfHomeStateAbsent(stateStore, path, settings.DryRun);
            SquadCommandComposition.WriteMigrationNotes(import);
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
    private static int WriteSummary(IReadOnlyList<ProjectOutcome> outcomes)
    {
        int updated = outcomes.Count(outcome => outcome.State == ProjectState.Updated);
        int failed = outcomes.Count(outcome => outcome.State == ProjectState.Failed);
        int skipped = outcomes.Count(outcome => outcome.State == ProjectState.Skipped);

        SquadCommandComposition.WriteLine(
            $"[bold]Project update summary:[/] {updated} updated, {failed} failed, {skipped} skipped.");
        foreach (ProjectOutcome outcome in outcomes)
        {
            string label = outcome.State switch
            {
                ProjectState.Updated => "[green]updated[/]",
                ProjectState.Failed => "[red]failed[/]",
                _ => "[yellow]skipped[/]"
            };
            SquadCommandComposition.WriteLine($"  {label} {Markup.Escape(outcome.Path)}");
        }

        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    ///     Updates one deployment: a global one, a project's own, or a project a global run selected. This is the
    ///     single project update path, so every form gets the same planning, confirmation and transaction.
    /// </summary>
    /// <returns>0 on success, 2 when the operator declined or the resolution refused, and 1 on failure.</returns>
    private int RunUpdate(
        UpdateRun run,
        SquadStateStore stateStore,
        SquadLifecycleService lifecycleService,
        bool interactive,
        CancellationToken cancellationToken)
    {
        SquadReceipt? receipt = stateStore.ReadReceipt(run.TargetRoot, run.Scope);
        if (receipt is null)
        {
            SquadCommandComposition.WriteLine(
                $"[red]No Kyber-Squad deployment found at [bold]{Markup.Escape(run.TargetRoot)}[/].[/]");
            SquadCommandComposition.WriteLine(
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
            receiptTargets = Array.Empty<SquadTarget>();
        }

        // Load configuration if present
        KyberWeaveConfigLoadResult configResult = KyberWeaveConfigLoader.TryLoad(run.TargetRoot);
        if (!configResult.Success)
        {
            SquadCommandComposition.WriteClientInputError(configResult.Error ?? "Failed to load configuration.");
            return 1;
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
            IsInteractive = SquadCommandComposition.IsInteractiveConsole()
        };

        SquadTargetResolutionDecision decision = SquadTargetResolver.Resolve(request);
        if (decision.Kind == SquadTargetResolutionKind.Failure)
        {
            AnsiConsole.MarkupLine("[red]No deployment targets specified or detected.[/]");
            if (decision.RecoveryCommand is not null)
                AnsiConsole.MarkupLine($"Specify target(s) using [bold]{Markup.Escape(decision.RecoveryCommand)}[/].");

            return decision.ExitCode ?? 2;
        }

        SquadUpdateRequest updateRequest = new(
            run.TargetRoot,
            run.Scope,
            decision.Targets,
            run.ExplicitExclusions,
            run.Version,
            ReplaceManaged: run.ReplaceManaged,
            DryRun: run.DryRun);

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
        if (!run.DryRun && !SquadTargetRootConfirmation.Confirm(
                run.TargetRoot,
                run.Scope,
                "update",
                interactive,
                run.Yes,
                _readAnswer ?? SquadTargetRootConfirmation.ReadConsoleAnswer,
                globalTargetRoots))
        {
            AnsiConsole.MarkupLine("[yellow]Declined. No changes were made.[/]");
            return 2;
        }

        try
        {
            SquadLifecycleResult result = lifecycleService.UpdateAsync(updateRequest, cancellationToken).GetAwaiter()
                .GetResult();
            if (result.Success)
            {
                if (run.DryRun)
                {
                    int fileCount = result.Receipt?.Files.Count ?? 0;
                    SquadCommandComposition.WriteLine(
                        $"[bold]Dry-run:[/] planned {fileCount} deployed files for [bold]{Markup.Escape(run.TargetRoot)}[/].");
                }
                else
                {
                    SquadCommandComposition.WriteLine(
                        $"[green]Successfully updated Kyber-Squad at [bold]{Markup.Escape(run.TargetRoot)}[/].[/]");
                }

                SquadCommandComposition.WriteTransactionWarnings(result.Warnings);
                return 0;
            }

            if (result.Errors is { Count: > 0 })
                foreach (string error in result.Errors)
                    SquadCommandComposition.WriteClientInputError(error);

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
