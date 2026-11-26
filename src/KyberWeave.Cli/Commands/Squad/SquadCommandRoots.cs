using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Text;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
///     The Squad verbs' shared step that reaches the disk and the process directory: it resolves the roots a run is
///     anchored at, brings legacy state into the home layout, reads the scope's configuration and names registered
///     projects, all through the file-system, path and environment seams.
/// </summary>
internal sealed class SquadCommandRoots
{
    private const string InRepoStateDirectoryName = UserStatePaths.DirectoryName;
    private const string ReceiptFileName = "squad.receipt.json";

    private readonly IWritableFileSystem _files;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;
    private readonly SquadConsole _output;

    /// <summary>Creates the roots over the seams the host registers.</summary>
    /// <param name="files">Probes the home, the project roots and their state, and creates a missing home.</param>
    /// <param name="paths">Resolves a path to its absolute form.</param>
    /// <param name="environment">Names the working directory a relative path resolves against.</param>
    /// <param name="output">Where the warnings a root resolution raises are written.</param>
    public SquadCommandRoots(
        IWritableFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        SquadConsole output)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(output);
        _files = files;
        _paths = paths;
        _environment = environment;
        _output = output;
    }

    /// <summary>Resolves the target root directory path against the working directory.</summary>
    /// <param name="path">The path the operator gave, or null or blank for the working directory.</param>
    /// <returns>The absolute root.</returns>
    public string ResolveTargetRoot(string? path) =>
        _paths.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path, _environment.CurrentDirectory);

    /// <summary>
    ///     Anchors a <c>--global</c> run. Global state is one home-level record, so where the operator launched
    ///     from does not change where it lives; a path supplied with the run is named and ignored rather than
    ///     silently honoured. A pre-#91 legacy single-root receipt is the exception: its files live beneath the
    ///     one root it was bound to, so that recorded root is the anchor.
    /// </summary>
    /// <remarks>
    ///     The anchor reads the home receipt, so it must be chosen after <see cref="EnsureStateMigrated" />. Before the
    ///     migration a legacy receipt is not yet in the home, and the home would be chosen for it, which is the wrong
    ///     root. Once the migration has copied the receipt, its target root is the absolute binding root the copy
    ///     recorded. Every later run then anchors on that same root, whatever directory it starts from. Anchoring on
    ///     the launch directory instead would verify, and could remove, files beneath the wrong root.
    /// </remarks>
    /// <param name="stateStore">The store the global receipt is read from. Its user paths name the home.</param>
    /// <param name="suppliedPath">The path the operator gave, or null when none was given.</param>
    /// <param name="dryRun">Whether the run only plans, so the home directory is not created.</param>
    /// <returns>The root the global run is anchored at.</returns>
    public string ResolveGlobalAnchorRoot(ISquadStateStore stateStore, string? suppliedPath, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        string home = _paths.GetFullPath(stateStore.UserPaths.HomeDirectory, _environment.CurrentDirectory);
        SquadReceipt? legacy = TryReadLegacySingleRootHomeReceipt(stateStore, home);
        if (legacy is not null)
        {
            if (Path.IsPathFullyQualified(legacy.TargetRoot))
            {
                if (suppliedPath is not null && !string.Equals(suppliedPath, ".", StringComparison.Ordinal))
                    _output.WriteLine(
                        $"[yellow]warning:[/] the path '{Markup.Escape(suppliedPath)}' is ignored under --global; " +
                        $"this legacy single-root deployment is anchored at its recorded root '{Markup.Escape(legacy.TargetRoot)}'.");

                return legacy.TargetRoot;
            }

            // A legacy receipt that records no absolute root names no root of its own, so the operator's path, or
            // the process directory when none is given, stays the anchor, as it was before the migration recorded one.
            return ResolveTargetRoot(suppliedPath);
        }

        // The home directory exists on every real profile; creating it here only matters for a fake home,
        // and a dry run writes nothing, so it is left alone there.
        if (!dryRun && !_files.DirectoryExists(home)) _files.CreateDirectory(home);

        if (suppliedPath is not null && !string.Equals(suppliedPath, ".", StringComparison.Ordinal))
            _output.WriteLine(
                $"[yellow]warning:[/] the path '{Markup.Escape(suppliedPath)}' is ignored under --global; " +
                $"global state lives beneath the home directory '{Markup.Escape(home)}'.");

        return home;
    }

    /// <summary>
    ///     Loads the host configuration a Squad verb reads. A global run always reads the home directory's
    ///     <c>.kyber-weave/kyber-weave.yml</c> as the user-level file that records <c>arbiter.global.targets</c>,
    ///     whatever root its receipt anchors at; reading it as a project config would reject that block as
    ///     <c>KW-ARB-CONFIG-011</c>. Only a project-scope run loads <paramref name="targetRoot" /> as a project.
    /// </summary>
    /// <param name="configs">The repository the configuration is read through.</param>
    /// <param name="targetRoot">The root the run is anchored at.</param>
    /// <param name="scope">Whether the run is global or project scope.</param>
    /// <param name="homeDirectory">The home directory the run's user paths name.</param>
    /// <returns>The load result, failed when a path cannot be resolved.</returns>
    public KyberWeaveConfigLoadResult TryLoadScopeConfig(
        IKyberWeaveConfigRepository configs,
        string targetRoot,
        SquadDeploymentScope scope,
        string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(configs);

        if (string.IsNullOrWhiteSpace(targetRoot))
            return KyberWeaveConfigLoadResultFactory.Fail("The target root must not be empty.", null);
        if (string.IsNullOrWhiteSpace(homeDirectory))
            return KyberWeaveConfigLoadResultFactory.Fail("The home directory must not be empty.", null);

        try
        {
            if (scope != SquadDeploymentScope.Global)
                return configs.TryLoad(targetRoot);

            // A global run always reads the home user-level config, whatever root its receipt anchors at (home, or
            // the root a legacy single-root receipt records), so the answer does not depend on how the path is spelled.
            string home = _paths.GetFullPath(homeDirectory, _environment.CurrentDirectory);
            KyberWeaveConfigLoadResult user = configs.TryLoadUserConfig(home);

            // The read reports the user path even when the file is absent, so existence is the repository's answer:
            // FindConfigPath names the user-level file, or the legacy root file, or nothing. A read of the legacy file
            // only happens when that is the file the home actually holds, so a user file is never re-probed as a
            // project config.
            string? found = configs.FindConfigPath(home);
            if (found is null
                || (user.ConfigPath is not null
                    && string.Equals(
                        _paths.GetFullPath(found, _environment.CurrentDirectory),
                        _paths.GetFullPath(user.ConfigPath, _environment.CurrentDirectory),
                        StringComparison.Ordinal)))
                return user;

            return configs.TryLoad(home, found);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return KyberWeaveConfigLoadResultFactory.Fail(ex.Message, null);
        }
    }

    /// <summary>
    ///     Brings legacy Squad state into the home-level layout before a verb reads it: the single legacy
    ///     global receipt and, when a project root is given, that project's in-repo state.
    /// </summary>
    /// <remarks>
    ///     Shared by every squad verb so no read ever sees the pre-home layout. Under a dry run the migration
    ///     writes nothing and only reports what the next non-dry-run invocation will move. The project root is
    ///     optional because global verbs have no project to import.
    /// </remarks>
    /// <param name="migration">Moves the legacy state.</param>
    /// <param name="invokingCwd">The directory the operator launched from.</param>
    /// <param name="projectRoot">The project whose in-repo state is imported, or null for a global verb.</param>
    /// <param name="dryRun">Whether the run only plans.</param>
    /// <returns>What the migration moved, imported and warned about.</returns>
    public SquadMigrationReport EnsureStateMigrated(
        ISquadStateMigration migration,
        string? invokingCwd,
        string? projectRoot,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(migration);

        SquadMigrationReport global = migration.MigrateGlobal(invokingCwd, dryRun);

        List<string> imported = [];
        List<string> warnings = [.. global.Warnings];
        if (projectRoot is not null && _files.DirectoryExists(projectRoot))
        {
            SquadMigrationReport project = migration.ImportProject(projectRoot, dryRun);
            imported.AddRange(project.ImportedProjects);
            // Both steps read the migration ledger, so an unreadable ledger would otherwise be reported twice.
            foreach (string warning in project.Warnings)
                if (!warnings.Contains(warning))
                    warnings.Add(warning);
        }

        return new SquadMigrationReport(global.MigratedItems, global.UnmigratedLegacyRoots, imported, warnings);
    }

    /// <summary>
    ///     Resolves each named project path to a registered project, or to an unregistered path that holds
    ///     in-repo state to import. Any other path is refused with the nearest registered path as a hint.
    /// </summary>
    /// <remarks>
    ///     Runs before any global work, so a typo fails the run with nothing written. The registry is matched
    ///     by canonical identity, so the operator can name a project through an alias or a link.
    /// </remarks>
    /// <param name="paths">The project paths the operator named.</param>
    /// <param name="stateStore">The registry the paths are matched against.</param>
    /// <returns>The distinct project roots, in the order named.</returns>
    /// <exception cref="ArgumentException">A path is neither registered nor importable.</exception>
    public IReadOnlyList<string> ResolveNamedProjects(IReadOnlyList<string> paths, ISquadStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(stateStore);

        List<string> resolved = [];
        foreach (string path in paths)
        {
            string full = _paths.GetFullPath(path, _environment.CurrentDirectory);
            SquadProjectRegistryEntry? entry = stateStore.FindProject(full);
            string chosen;
            if (entry is not null)
                chosen = entry.Path;
            else if (_files.DirectoryExists(full) &&
                     _files.FileExists(Path.Combine(full, InRepoStateDirectoryName, ReceiptFileName)))
                chosen = full;
            else
                throw new ArgumentException(UnknownProjectMessage(full, stateStore.ListProjects()));

            if (!resolved.Contains(chosen, StringComparer.Ordinal)) resolved.Add(chosen);
        }

        return resolved;
    }

    /// <summary>
    ///     The home global receipt when it is a pre-#91 single-root receipt, whose files resolve beneath the one
    ///     root it records rather than beneath each target's own global root. Null when there is no such receipt.
    /// </summary>
    private static SquadReceipt? TryReadLegacySingleRootHomeReceipt(ISquadStateStore stateStore, string home)
    {
        try
        {
            SquadReceipt? receipt = stateStore.ReadReceipt(home, SquadDeploymentScope.Global);
            return receipt is not null && SquadDeploymentPlanRules.IsLegacySingleRootReceipt(receipt) ? receipt : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
        {
            // A corrupt or blank receipt is reported by the command that reads it; it does not pick an anchor here.
            return null;
        }
    }

    /// <summary>
    ///     The refusal for an unknown project path. Names the nearest registered path so a typo is one
    ///     copy-paste from being fixed, and says plainly when nothing is registered at all.
    /// </summary>
    private static string UnknownProjectMessage(string full, IReadOnlyList<SquadProjectRegistryEntry> registered)
    {
        string message =
            $"'{full}' is not a registered Squad project, and it holds no in-repo Squad state to import.";
        if (registered.Count == 0) return message + " No projects are registered.";

        SquadProjectRegistryEntry nearest = registered.MinBy(entry =>
            StringDistance.Levenshtein(full, entry.Path, true))!;
        return message + $" Did you mean '{nearest.Path}'?";
    }
}
