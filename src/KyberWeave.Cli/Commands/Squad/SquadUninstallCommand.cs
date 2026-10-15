using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Uninstalls Kyber-Squad from a project or global deployment directory using its ownership receipt.
/// </summary>
public sealed class SquadUninstallCommand : Command<SquadUninstallSettings>
{
    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;
    private readonly SquadLifecycleService? _lifecycleService;
    private readonly bool? _isInteractive;
    private readonly Func<string, bool>? _readAnswer;
    private readonly ISquadGlobalRootResolver? _globalRoots;

    /// <summary>Creates a new uninstall command using default user paths.</summary>
    public SquadUninstallCommand()
    {
    }

    /// <summary>Creates a new uninstall command using injectable dependencies.</summary>
    /// <remarks>
    /// <paramref name="isInteractive"/> and <paramref name="readAnswer"/> default to null
    /// because optional parameter values must be compile-time constants; Execute resolves
    /// them to <see cref="SquadCommandComposition.IsInteractiveConsole"/> and real console
    /// input, so an unparameterized run behaves exactly like the default collaborators.
    /// </remarks>
    internal SquadUninstallCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        SquadLifecycleService? lifecycleService = null,
        bool? isInteractive = null,
        Func<string, bool>? readAnswer = null,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _lifecycleService = lifecycleService;
        _isInteractive = isInteractive;
        _readAnswer = readAnswer;
        _globalRoots = globalRoots;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadUninstallSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);

        // Coalesce the positional path with --path; invalid client input returns exit
        // code 2 before any resolution or work
        string? effectivePath;
        try
        {
            effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

        string projectRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);

        // Legacy state is migrated before the uninstall reads the receipt it removes.
        SquadMigrationReport migration = SquadCommandComposition.EnsureStateMigrated(
            stateStore,
            Environment.CurrentDirectory,
            projectRoot: scope == SquadDeploymentScope.Project ? projectRoot : null,
            dryRun: settings.DryRun);
        SquadCommandComposition.WriteMigrationNotes(migration);
        if (SquadCommandComposition.IsPendingDryRunMigration(migration, settings.DryRun))
        {
            return 0;
        }

        // A --global uninstall touches the home-level global deployment only: it is anchored at the home directory
        // whatever the working directory is, or at the recorded root of a legacy single-root receipt. It never reads
        // or changes a project or the registry. Chosen after the migration, so the receipt it removes is the migrated one.
        string targetRoot = scope == SquadDeploymentScope.Global
            ? SquadCommandComposition.ResolveGlobalAnchorRoot(stateStore, effectivePath, dryRun: settings.DryRun)
            : projectRoot;

        SquadLifecycleService lifecycleService = _lifecycleService ?? SquadCommandComposition.CreateLifecycleService(
            userPaths: _userPaths,
            stateStore: stateStore,
            globalRoots: _globalRoots);

        SquadUninstallRequest uninstallRequest = new(
            TargetRoot: targetRoot,
            Scope: scope,
            DryRun: settings.DryRun);

        // Uninstall has no --target option: its targets are the receipt's. A --global
        // receipt's files live beneath each target's own physical global root, not beneath
        // targetRoot — the state anchor — so the roots the lifecycle will write are
        // derived from the same receipt and named in the confirmation.
        IReadOnlyList<(SquadTarget Target, string GlobalRoot)>? globalTargetRoots =
            scope == SquadDeploymentScope.Global
                ? SquadCommandComposition.ResolveUninstallGlobalTargetRoots(stateStore, targetRoot, _globalRoots)
                : null;

        // The confirmation is the last gate before the lifecycle call — the only
        // side-effecting step — so a decline aborts with zero writes (plan N3/N5);
        // a dry-run's output already names the root, so it never prompts (N4).
        if (!settings.DryRun && !SquadTargetRootConfirmation.Confirm(
                targetRoot,
                scope,
                "uninstall",
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
            SquadLifecycleResult result = lifecycleService.UninstallAsync(uninstallRequest, cancellationToken).GetAwaiter().GetResult();
            if (result.Success)
            {
                if (result.Plan is null)
                {
                    AnsiConsole.MarkupLine($"[grey]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/]. Nothing to uninstall.[/]");
                    return 0;
                }

                if (settings.DryRun)
                {
                    // An uninstall plan's receipt holds only the files it retains; the
                    // removals are the plan's Delete changes, which also carry the target
                    // a global deployment removes each path from.
                    IReadOnlyList<SquadPlannedFileChange> removals = result.Plan.PlannedFileChanges
                        .Where(change => change.Kind == SquadFileMutationKind.Delete)
                        .ToArray();
                    AnsiConsole.MarkupLine($"[bold]Dry-run:[/] would uninstall {removals.Count} files from [bold]{Markup.Escape(targetRoot)}[/]:");
                    foreach (SquadPlannedFileChange removal in removals)
                    {
                        AnsiConsole.MarkupLine($"  [red]remove[/] {Markup.Escape(removal.RelativePath)} ({Markup.Escape(removal.Target)})");
                    }

                    return 0;
                }

                SquadCommandComposition.WriteLine($"[green]Successfully uninstalled Kyber-Squad from [bold]{Markup.Escape(targetRoot)}[/].[/]");
                SquadCommandComposition.WriteTransactionWarnings(result.Warnings);
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
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    public int Execute(CommandContext context, SquadUninstallSettings settings) => Execute(context, settings, CancellationToken.None);
}
