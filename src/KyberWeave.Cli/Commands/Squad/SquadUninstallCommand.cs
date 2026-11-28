using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Uninstalls Kyber-Squad from a project or global deployment directory using its ownership receipt.
/// </summary>
internal sealed class SquadUninstallCommand : Command<SquadUninstallSettings>
{
    private readonly ISquadLifecycleService _lifecycle;
    private readonly ISquadStateStore _stateStore;
    private readonly ISquadStateMigration _migration;
    private readonly ISquadGlobalRootResolver _globalRoots;
    private readonly SquadConsoleOptions _console;
    private readonly SquadConsole _output;
    private readonly SquadTargetRootConfirmation _confirmation;
    private readonly IProcessEnvironment _environment;
    private readonly SquadCommandRoots _roots;

    /// <summary>Creates the uninstall command over the Squad services the host registers.</summary>
    /// <param name="lifecycle">Removes the deployment.</param>
    /// <param name="stateStore">Reads the deployment state.</param>
    /// <param name="migration">Moves legacy state into the home layout before the uninstall reads it.</param>
    /// <param name="globalRoots">Names the roots a global uninstall writes beneath.</param>
    /// <param name="console">How the command prompts; the defaults read the console.</param>
    /// <param name="output">Where the command writes and asks.</param>
    /// <param name="confirmation">Echoes the target root and asks before anything is written.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    /// <param name="roots">Resolves the target root, anchors a global run and reads the scope's configuration.</param>
    public SquadUninstallCommand(
        ISquadLifecycleService lifecycle,
        ISquadStateStore stateStore,
        ISquadStateMigration migration,
        ISquadGlobalRootResolver globalRoots,
        SquadConsoleOptions console,
        SquadConsole output,
        SquadTargetRootConfirmation confirmation,
        IProcessEnvironment environment,
        SquadCommandRoots roots)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(globalRoots);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(roots);
        _lifecycle = lifecycle;
        _stateStore = stateStore;
        _migration = migration;
        _globalRoots = globalRoots;
        _console = console;
        _output = output;
        _confirmation = confirmation;
        _environment = environment;
        _roots = roots;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadUninstallSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ISquadStateStore stateStore = _stateStore;

        // Invalid client input returns exit code 2 before any resolution or work.
        if (!TryResolveTarget(settings, out SquadTarget? target))
        {
            return 2;
        }

        // A named harness defaults to the global scope, as `squad install --target ... -g` does; --project
        // selects the local project. With no harness the project stays the default, as it always was.
        bool isGlobal = settings.Global || (target is not null && !settings.Project);

        string? effectivePath = settings.PathOption;
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(isGlobal);

        string projectRoot = _roots.ResolveTargetRoot(effectivePath);

        // Legacy state is migrated before the uninstall reads the receipt it removes.
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

        // A --global uninstall touches the home-level global deployment only: it is anchored at the home directory
        // whatever the working directory is, or at the recorded root of a legacy single-root receipt. It never reads
        // or changes a project or the registry. Chosen after the migration, so the receipt it removes is the migrated one.
        string targetRoot = scope == SquadDeploymentScope.Global
            ? _roots.ResolveGlobalAnchorRoot(stateStore, effectivePath, settings.DryRun)
            : projectRoot;

        ISquadLifecycleService lifecycleService = _lifecycle;

        if (target is { } chosen
            && !SquadCommandComposition.ReceiptHasTarget(stateStore, targetRoot, scope, chosen))
        {
            _output.WriteClientInputError(
                $"Harness '{SquadTargetCatalog.GetToken(chosen)}' is not installed in the {(scope == SquadDeploymentScope.Global ? "global" : "project")} " +
                $"deployment at '{targetRoot}'. Run `kyber-weave squad status{(scope == SquadDeploymentScope.Global ? " --global" : string.Empty)}` to see what is installed.");
            return 2;
        }

        SquadUninstallRequest uninstallRequest = new(
            targetRoot,
            scope,
            settings.DryRun,
            target);

        // Uninstall's targets are the receipt's, narrowed to the named harness when one is given. A --global
        // receipt's files live beneath each target's own physical global root, not beneath
        // targetRoot — the state anchor — so the roots the lifecycle will write are
        // derived from the same receipt and named in the confirmation.
        IReadOnlyList<(SquadTarget Target, string GlobalRoot)>? globalTargetRoots =
            scope == SquadDeploymentScope.Global
                ? SquadCommandComposition.ResolveUninstallGlobalTargetRoots(stateStore, targetRoot, _globalRoots, target)
                : null;

        // The confirmation is the last gate before the lifecycle call — the only
        // side-effecting step — so a decline aborts with zero writes (plan N3/N5);
        // a dry-run's output already names the root, so it never prompts (N4).
        if (!settings.DryRun && !_confirmation.Confirm(
                targetRoot,
                scope,
                "uninstall",
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
            SquadLifecycleResult result = lifecycleService.UninstallAsync(uninstallRequest, cancellationToken)
                .GetAwaiter().GetResult();
            return WriteUninstallResult(result, settings, targetRoot);
        }
        catch (Exception ex)
        {
            _output.WriteFailure(ex);
            return 1;
        }
    }

    public int Execute(CommandContext context, SquadUninstallSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    private bool TryResolveTarget(SquadUninstallSettings settings, out SquadTarget? target)
    {
        target = null;

        if (settings.Project && settings.Global)
        {
            _output.WriteClientInputError(
                "--global and --project are mutually exclusive. Omit both to use the default scope.");
            return false;
        }

        if (settings.Harness is not null)
        {
            IReadOnlyList<SquadTarget> parsed;
            try
            {
                parsed = SquadTargetCatalog.Parse([settings.Harness]);
            }
            catch (ArgumentException ex)
            {
                _output.WriteClientInputError(
                    $"{ex.Message} The argument names a harness, not a directory: to uninstall a project directory, " +
                    "use `--project` for the current directory or `--project --path <dir>`.");
                return false;
            }

            if (parsed.Count != 1)
            {
                _output.WriteClientInputError(
                    "Name exactly one harness to uninstall, or omit it to remove every harness in the receipt.");
                return false;
            }

            target = parsed[0];
        }

        if (!string.IsNullOrWhiteSpace(settings.PathOption) && target is not null && !settings.Project)
        {
            _output.WriteClientInputError(
                "--path is only valid with --project when a harness is named. Add --project to target a project root.");
            return false;
        }

        if (settings.PathOption is not null && string.IsNullOrWhiteSpace(settings.PathOption))
        {
            _output.WriteClientInputError("--path must not be blank. Give a directory, or omit the option.");
            return false;
        }

        return true;
    }

    private int WriteUninstallResult(
        SquadLifecycleResult result,
        SquadUninstallSettings settings,
        string targetRoot)
    {
        if (result.Success)
        {
            if (result.Plan is null)
            {
                _output.Ansi.MarkupLine(
                    $"[grey]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/]. Nothing to uninstall.[/]");
                _output.WriteTransactionWarnings(result.Warnings);
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
                _output.Ansi.MarkupLine(
                    $"[bold]Dry-run:[/] would uninstall {removals.Count} files from [bold]{Markup.Escape(targetRoot)}[/]:");
                foreach (SquadPlannedFileChange removal in removals)
                {
                    _output.Ansi.MarkupLine(
                        $"  [red]remove[/] {Markup.Escape(removal.RelativePath)} ({Markup.Escape(removal.Target)})");
                }

                _output.WriteTransactionWarnings(result.Warnings);
                return 0;
            }

            _output.WriteLine(
                $"[green]Successfully uninstalled Kyber-Squad from [bold]{Markup.Escape(targetRoot)}[/].[/]");
            _output.WriteTransactionWarnings(result.Warnings);
            return 0;
        }

        if (result.Errors is { Count: > 0 })
        {
            foreach (string error in result.Errors)
            {
                _output.Ansi.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(error)}[/]");
            }
        }

        return 1;
    }
}
