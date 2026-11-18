using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Composition;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Core.Text;
using Spectre.Console;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>Which projects a <c>--projects</c> value selects.</summary>
internal enum SquadProjectSelectionKind
{
    /// <summary>No <c>--projects</c> was given.</summary>
    Absent,

    /// <summary>Nothing is updated beyond the global run.</summary>
    None,

    /// <summary>Every registered project that is not stale.</summary>
    All,

    /// <summary>The named project paths.</summary>
    Paths
}

/// <summary>The parsed <c>--projects</c> selection.</summary>
/// <param name="Kind">Which selection the operator asked for.</param>
/// <param name="Paths">The named project paths, populated only for <see cref="SquadProjectSelectionKind.Paths" />.</param>
internal sealed record SquadProjectSelection(SquadProjectSelectionKind Kind, IReadOnlyList<string> Paths);

/// <summary>
///     CLI composition root for Squad commands: provides factory and resolution methods
///     to resolve roots, scope, state stores, transactions, process probes, and console context.
/// </summary>
internal static class SquadCommandComposition
{
    private const string ProjectKeywordAll = "all";
    private const string ProjectKeywordNone = "none";
    private const string InRepoStateDirectoryName = ".kyber-weave";
    private const string ReceiptFileName = "squad.receipt.json";

    /// <summary>Indicates whether the console environment supports interactive input.</summary>
    public static bool IsInteractiveConsole()
    {
        return SquadCliServices.ProcessConsole().IsInteractive;
    }

    /// <summary>
    ///     Writes the exit-2 client-input error line without console folding. The hint names
    ///     the operator's paths verbatim, and a redirected console folds at 80 columns,
    ///     splitting a long path across lines — corrupting the one thing the hint exists
    ///     for: a copy-pastable name for each of the conflicting forms.
    /// </summary>
    public static void WriteClientInputError(string message)
    {
        SquadCliServices.ProcessConsole().WriteClientInputError(message);
    }

    /// <summary>
    ///     Resolves each selected target's physical global root — the directory a
    ///     <c>--global</c> lifecycle actually writes — through the same resolver (and
    ///     therefore the same override environment variables, <c>CODEX_HOME</c>,
    ///     <c>CLAUDE_CONFIG_DIR</c>, …) the lifecycle's plan resolves with moments later.
    /// </summary>
    /// <remarks>
    ///     A target whose root cannot be resolved — no verified per-user directory, or an
    ///     override naming a relative path — is skipped rather than thrown: the confirmation
    ///     gate must still name the roots that would genuinely change, and the lifecycle call
    ///     after it remains the step that reports an unresolvable root as a failure.
    /// </remarks>
    public static IReadOnlyList<(SquadTarget Target, string GlobalRoot)> ResolveGlobalTargetRoots(
        IReadOnlyList<SquadTarget> targets,
        ISquadGlobalRootResolver globalRoots)
    {
        // The resolver is the one the lifecycle plans with, so the confirmation names the same roots.
        List<(SquadTarget Target, string GlobalRoot)> resolved = [];
        foreach (SquadTarget target in targets)
            try
            {
                resolved.Add((target, globalRoots.ResolveGlobalRoot(target)));
            }
            catch (ArgumentOutOfRangeException)
            {
                // No verified per-user directory for this target: nothing can be named,
                // and the lifecycle call after the gate still fails the run for it.
            }
            catch (ArgumentException)
            {
                // The target's override resolves to a relative path: same treatment.
            }

        return resolved;
    }

    /// <summary>
    ///     Derives the per-target global roots a <c>--global</c> uninstall will write from the
    ///     deployment receipt: uninstall takes no target list, so the receipt's owned
    ///     files are the authoritative target list, and the lifecycle resolves each through
    ///     <see cref="ISquadGlobalRootResolver.ResolveGlobalRoot" /> at plan time — unless the
    ///     receipt is a legacy single-root receipt (#91), in which case every target actually
    ///     writes beneath the recorded <paramref name="targetRoot" />, and naming the resolver's
    ///     per-target answer instead would confirm roots the uninstall will not touch.
    /// </summary>
    /// <remarks>
    ///     Every failure mode degrades to "no per-target roots to name" rather than throwing:
    ///     an absent receipt means nothing will be uninstalled, and a corrupt receipt or an
    ///     unknown target token is surfaced by the lifecycle call after the gate, which reads
    ///     the same state and fails under the command's exit-1 convention.
    /// </remarks>
    public static IReadOnlyList<(SquadTarget Target, string GlobalRoot)> ResolveUninstallGlobalTargetRoots(
        ISquadStateStore stateStore,
        string targetRoot,
        ISquadGlobalRootResolver injectedGlobalRoots,
        SquadTarget? onlyTarget = null)
    {
        SquadReceipt? receipt;
        try
        {
            receipt = stateStore.ReadReceipt(targetRoot, SquadDeploymentScope.Global);
        }
        catch (InvalidDataException)
        {
            return [];
        }

        if (receipt is null) return [];

        IReadOnlyList<SquadTarget> targets;
        try
        {
            targets = SquadTargetCatalog.Parse(receipt.Files.Select(file => file.Target).Distinct());
        }
        catch (ArgumentException)
        {
            return [];
        }

        if (onlyTarget is { } chosen)
            targets = [.. targets.Where(target => target == chosen)];

        if (SquadDeploymentPlan.IsLegacySingleRootReceipt(receipt))
            return [.. targets.Select(target => (target, targetRoot))];

        return ResolveGlobalTargetRoots(targets, injectedGlobalRoots);
    }

    /// <summary>
    ///     Whether the receipt at <paramref name="targetRoot" /> records any file or block for
    ///     <paramref name="target" />. An absent receipt answers false: the uninstall then reports the
    ///     harness as not installed. A receipt that cannot be read (corrupt, or a file-system failure) answers
    ///     true so the lifecycle call surfaces the real error instead of a misleading "not installed".
    /// </summary>
    public static bool ReceiptHasTarget(
        ISquadStateStore stateStore,
        string targetRoot,
        SquadDeploymentScope scope,
        SquadTarget target)
    {
        SquadReceipt? receipt;
        try
        {
            receipt = stateStore.ReadReceipt(targetRoot, scope);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
        {
            return true;
        }

        string token = SquadTargetCatalog.GetToken(target);
        return receipt is not null
               && (receipt.Files.Any(file => file.Target == token) || receipt.Blocks.Any(block => block.Target == token));
    }

    /// <summary>
    ///     The file-reading port <see cref="SquadGlobalRoots" /> uses for the one target whose
    ///     global root can be set in a config file rather than an environment variable. Absent or
    ///     unreadable is not an error: the resolver falls back to that target's default, matching
    ///     what the harness itself does with the same file.
    /// </summary>
    internal static string? ReadFileTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }

    /// <summary>Resolves the Kyber-Weave MCP process probe using the specified process executor.</summary>
    public static McpProcessProbe ResolveProbe(IProcessExecutor? executor)
    {
        return new McpProcessProbe(executor ?? ProcessExecutor.Instance);
    }

    /// <summary>Resolves the Kyber-Weave MCP process probe using the default process executor.</summary>
    public static McpProcessProbe ResolveProbe()
    {
        return ResolveProbe(null);
    }

    /// <summary>Resolves the Kyber-Weave Arbiter process probe using the specified process executor.</summary>
    public static ArbiterProcessProbe ResolveArbiterProbe(IProcessExecutor? executor) =>
        new(executor ?? ProcessExecutor.Instance);

    /// <summary>Resolves the Kyber-Weave Arbiter process probe using the default process executor.</summary>
    public static ArbiterProcessProbe ResolveArbiterProbe() => ResolveArbiterProbe(null);

    /// <summary>
    /// Carries the host's Arbiter settings into a render: the enabled flag verbatim and the
    /// provider timeout converted from milliseconds to whole hook seconds with headroom.
    /// </summary>
    /// <remarks>
    /// The hook timeout is ceil(timeout-ms / 1000) + 2 (Req 22.2, 5.3): whole seconds for
    /// the harness hook contract plus two seconds of dispatch headroom beyond the provider
    /// budget.
    /// </remarks>
    public static SquadArbiterWiring ResolveArbiterWiring(Core.Arbiter.ArbiterConfig arbiter)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        return new SquadArbiterWiring(arbiter.Enabled, Core.Arbiter.ArbiterHookTimeout.SecondsFor(arbiter.Provider.TimeoutMs));
    }

    /// <summary>
    ///     Writes the shared exit-1 operational error the preset verbs report when canonical model
    ///     profiles resolve from neither a checkout nor the home snapshot store.
    /// </summary>
    /// <remarks>
    ///     One message shape across use, clear, and show, so the remedy the operator learns from
    ///     one verb holds for the others: a <c>squad update</c> re-renders from the active
    ///     release and persists the snapshot the verbs read back.
    /// </remarks>
    public static void WriteCanonicalProfilesUnresolvedError()
    {
        WriteClientInputError(
            "Could not resolve Squad model profiles from canonical source. " +
            "Run `kyber-weave squad update` to refresh the profile snapshot; " +
            "running from a kyber-weave checkout also works.");
    }

    /// <summary>
    ///     Formats a preset validation failure the way the documented strict-validation contract
    ///     requires: the validator's finding with the preset file path and line number.
    /// </summary>
    /// <param name="exception">The validation failure the preset validator raised.</param>
    /// <returns>The finding message with its file-path and line-number location.</returns>
    /// <remarks>
    ///     The exception message alone names only the finding; the path and line live on the
    ///     first diagnostic, so the contract's "exact file path and line number" needs both.
    ///     A failure without a located diagnostic falls back to the exception message rather
    ///     than losing the finding.
    /// </remarks>
    public static string FormatPresetValidationError(SquadSourceValidationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        IReadOnlyList<Diagnostic> items = exception.Diagnostics.Items;
        if (items.Count == 0)
            return exception.Message;
        Diagnostic first = items[0];
        if (string.IsNullOrWhiteSpace(first.FilePath))
            return exception.Message;
        string filePath = first.FilePath;
        string location = first.StartLine.HasValue
            ? $"{filePath}:{first.StartLine.Value}"
            : filePath;
        return $"{first.Message} ({location})";
    }

    /// <summary>
    ///     Coalesces the positional path argument with the <c>--path</c> option into the one
    ///     effective target path. The option wins when the positional is absent or at its
    ///     <c>"."</c> default; the positional passes through when no option was supplied;
    ///     supplying both a non-default positional and the option is a conflict.
    /// </summary>
    /// <remarks>
    ///     Spectre.Console.Cli cannot carry a <c>[CommandArgument]</c> and a
    ///     <c>[CommandOption]</c> on one property, so <c>--path</c> binds a separate nullable
    ///     <c>PathOption</c> and this is the single seam where the two forms meet. The conflict
    ///     throws <see cref="ArgumentException" /> so it flows through the commands' existing
    ///     client-input catch into the exit-2 convention, and its hint names both forms so the
    ///     operator can pick one.
    /// </remarks>
    public static string? CoalesceTargetPath(string? positional, string? option)
    {
        if (string.IsNullOrWhiteSpace(option)) return positional;

        bool positionalIsDefault =
            string.IsNullOrWhiteSpace(positional) || string.Equals(positional, ".", StringComparison.Ordinal);
        if (positionalIsDefault) return option;

        throw new ArgumentException(
            $"The deployment root was supplied twice: as the positional '{positional}' and as the option '--path {option}'. " +
            "Supply either the positional path or --path <PATH>, not both.");
    }

    /// <summary>
    ///     Normalizes a pinned <c>-v|--version</c> value into the release version to request,
    ///     or <c>null</c> when the flag is omitted so the lifecycle's default version
    ///     resolution applies unchanged.
    /// </summary>
    /// <remarks>
    ///     Null is reserved for the omitted flag — it is the only input that maps to
    ///     <c>null</c>, so the lifecycle's default version resolution applies unchanged. A
    ///     supplied-but-blank value is refused instead of falling back to that same default,
    ///     which would silently deploy a version the operator did not ask for. Three gates,
    ///     in this order, then hold every invalid pinned version on the commands'
    ///     exit-2 client-input convention before root resolution and before any network call.
    ///     The build-metadata check runs first because <see cref="ReleaseVersion.Normalize" />
    ///     strips a '+build' suffix blind: without it, '1.2.3+' or '1.2.3+a..b' would
    ///     silently canonicalize to '1.2.3' and a release request would be issued for a
    ///     version the operator did not quite ask for. <see cref="ReleaseVersion.Normalize" />
    ///     then applies the looser, canonicalizing rule: it strips a leading 'v' and '+build'
    ///     metadata (so 'v1.2.3+sha' becomes '1.2.3') and rejects input that is not a Release
    ///     tag shape at all. The strict SemVer check runs last and reuses
    ///     <see cref="ReleaseVersion.IsStrictSemVer" /> — the one rule the
    ///     release source itself enforces, reused rather than duplicated so the two cannot
    ///     diverge — to reject tag-shaped forms the stricter rule still forbids, like '1.0'
    ///     or '01.2.3'. The release source keeps its own check as defense-in-depth.
    /// </remarks>
    /// <exception cref="SelfUpdateException">The value is not a Release tag shape.</exception>
    /// <exception cref="ArgumentException">
    ///     The value is empty or whitespace-only, carries malformed build metadata, or is
    ///     tag-shaped but not strict SemVer.
    /// </exception>
    public static string? NormalizePinnedVersion(string? version)
    {
        if (version is null) return null;

        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException(
                "--version was supplied but is empty; give a release version (X.Y.Z or X.Y.Z-prerelease) " +
                "or omit the flag to deploy the running CLI's own version.");

        EnsureValidBuildMetadata(version);
        string normalized = ReleaseVersion.Normalize(version);
        if (!ReleaseVersion.IsStrictSemVer(normalized))
            throw new ArgumentException(
                "--version must be a semantic version (X.Y.Z or X.Y.Z-prerelease); " +
                "a leading 'v' and '+build' metadata are accepted and stripped. " +
                $"Got '{version}'.");

        return normalized;
    }

    /// <summary>
    ///     Rejects malformed SemVer build metadata in a pinned <c>--version</c> value before
    ///     <see cref="ReleaseVersion.Normalize" /> strips the suffix.
    /// </summary>
    /// <remarks>
    ///     Normalize truncates at the first '+' without validating what follows, so a value
    ///     like '1.2.3+' or '1.2.3+a..b' would be silently canonicalized to '1.2.3'. Per
    ///     SemVer 2.0.0, build metadata is dot-separated identifiers, each non-empty and
    ///     drawn from ASCII letters, digits, and hyphens; anything else is refused on the
    ///     commands' exit-2 convention rather than canonicalized away. The suffix is read
    ///     from the trimmed value because that is the text Normalize itself strips.
    /// </remarks>
    private static void EnsureValidBuildMetadata(string version)
    {
        string trimmed = version.Trim();
        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return;

        string buildMetadata = trimmed[(plus + 1)..];
        bool isValid = buildMetadata.Split('.').All(identifier =>
            identifier.Length > 0 && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-'));
        if (!isValid)
            throw new ArgumentException(
                "--version build metadata must be dot-separated identifiers of ASCII letters, digits, and hyphens, " +
                "each at least one character long (X.Y.Z+build.metadata). " +
                $"Got '{version}'.");
    }

    /// <summary>Writes one line of console output without folding, so a path in it stays copy-pastable.</summary>
    public static void WriteLine(string markup)
    {
        SquadCliServices.ProcessConsole().WriteLine(markup);
    }

    /// <summary>
    ///     Surfaces the warnings a committed transaction raised after its commit point. The deployment itself
    ///     succeeded, so the command still exits 0, but the operator must learn that the registry is behind.
    /// </summary>
    /// <remarks>
    ///     Each warning already names the command that rebuilds the registry, so it is printed as written rather
    ///     than with a second instruction appended to it.
    /// </remarks>
    public static void WriteTransactionWarnings(IReadOnlyList<string>? warnings)
    {
        foreach (string warning in warnings ?? []) WriteLine($"[yellow]warning:[/] {Markup.Escape(warning)}");
    }

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
    /// <param name="files">The file system the home is probed and created through; null uses the process's own.</param>
    /// <param name="console">Where the ignored-path warning is written; null uses the process's own console.</param>
    public static string ResolveGlobalAnchorRoot(
        ISquadStateStore stateStore,
        string? suppliedPath,
        bool dryRun,
        IWritableFileSystem? files = null,
        SquadConsole? console = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);

        IWritableFileSystem fileSystem = files ?? SquadCliServices.ProcessFileSystem();
        SquadConsole output = console ?? SquadCliServices.ProcessConsole();
        string home = Path.GetFullPath(stateStore.UserPaths.HomeDirectory);
        SquadReceipt? legacy = TryReadLegacySingleRootHomeReceipt(stateStore, home);
        if (legacy is not null)
        {
            if (Path.IsPathFullyQualified(legacy.TargetRoot))
            {
                if (suppliedPath is not null && !string.Equals(suppliedPath, ".", StringComparison.Ordinal))
                    output.WriteLine(
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
        if (!dryRun && !fileSystem.DirectoryExists(home)) fileSystem.CreateDirectory(home);

        if (suppliedPath is not null && !string.Equals(suppliedPath, ".", StringComparison.Ordinal))
            output.WriteLine(
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
    public static KyberWeaveConfigLoadResult TryLoadScopeConfig(
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
            string home = Path.GetFullPath(homeDirectory);
            KyberWeaveConfigLoadResult user = configs.TryLoadUserConfig(home);

            // The read reports the user path even when the file is absent, so existence is the repository's answer:
            // FindConfigPath names the user-level file, or the legacy root file, or nothing. A read of the legacy file
            // only happens when that is the file the home actually holds, so a user file is never re-probed as a
            // project config.
            string? found = configs.FindConfigPath(home);
            if (found is null
                || (user.ConfigPath is not null
                    && string.Equals(Path.GetFullPath(found), Path.GetFullPath(user.ConfigPath), StringComparison.Ordinal)))
                return user;

            return configs.TryLoad(home, found);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return KyberWeaveConfigLoadResultFactory.Fail(ex.Message, null);
        }
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
            return receipt is not null && SquadDeploymentPlan.IsLegacySingleRootReceipt(receipt) ? receipt : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
        {
            // A corrupt or blank receipt is reported by the command that reads it; it does not pick an anchor here.
            return null;
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
    /// <param name="migration">The migration that moves the legacy state.</param>
    /// <param name="invokingCwd">The directory the command was started from, or null.</param>
    /// <param name="projectRoot">The project root to import, or null for a global verb.</param>
    /// <param name="dryRun">Whether the run only plans, so nothing is written.</param>
    /// <param name="files">The file system the project root is probed through; null uses the process's own.</param>
    public static SquadMigrationReport EnsureStateMigrated(
        ISquadStateMigration migration,
        string? invokingCwd,
        string? projectRoot,
        bool dryRun,
        IFileSystem? files = null)
    {
        ArgumentNullException.ThrowIfNull(migration);

        IFileSystem fileSystem = files ?? SquadCliServices.ProcessFileSystem();
        SquadMigrationReport global = migration.MigrateGlobal(invokingCwd, dryRun);

        List<string> imported = [];
        List<string> warnings = [.. global.Warnings];
        if (projectRoot is not null && fileSystem.DirectoryExists(projectRoot))
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
    ///     True when a dry run found legacy state that a real run would migrate. The verb reports that and
    ///     stops, so it never reads a deployment the migration has not yet moved.
    /// </summary>
    public static bool IsPendingDryRunMigration(SquadMigrationReport report, bool dryRun)
    {
        return dryRun && (report.MigratedItems.Count > 0 || report.ImportedProjects.Count > 0);
    }

    /// <summary>
    ///     Prints what the migration did and warns about, each line unfolded. Legacy roots that remain on disk
    ///     are printed only when <paramref name="includeRetainedLegacyRoots" /> is set: after every successful
    ///     migration they remain, so printing them from each verb would warn on every run. Doctor, which reports
    ///     what was left behind, is the one caller that asks for them.
    /// </summary>
    public static void WriteMigrationNotes(SquadMigrationReport report, bool includeRetainedLegacyRoots = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        foreach (string item in report.MigratedItems) WriteLine($"[grey]info[/] {Markup.Escape(item)}");

        foreach (string item in report.ImportedProjects) WriteLine($"[grey]info[/] {Markup.Escape(item)}");

        foreach (string warning in report.Warnings) WriteLine($"[yellow]warning:[/] {Markup.Escape(warning)}");

        if (!includeRetainedLegacyRoots) return;

        foreach (SquadUnmigratedLegacyRoot root in report.UnmigratedLegacyRoots)
            WriteLine(
                $"[yellow]warning:[/] legacy global root '{Markup.Escape(root.Root)}' was not migrated: " +
                $"{Markup.Escape(root.Reason)} {Markup.Escape(root.Guidance)}");
    }

    /// <summary>
    ///     Imports one project's in-repo state into the home registry when its home state is absent. A project
    ///     whose home receipt already exists is left alone, because the home receipt is the authority.
    /// </summary>
    /// <param name="stateStore">The home state store whose receipt decides whether the import is needed.</param>
    /// <param name="migration">Imports the project into the home registry.</param>
    /// <param name="root">The project root whose in-repo state may be imported.</param>
    /// <param name="dryRun">Whether the run only plans. A dry run writes nothing and reports the import it would make.</param>
    public static SquadMigrationReport ImportProjectIfHomeStateAbsent(
        ISquadStateStore stateStore,
        ISquadStateMigration migration,
        string root,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(migration);

        if (stateStore.ReadReceipt(root, SquadDeploymentScope.Project) is not null)
            return new SquadMigrationReport([], [], [], []);

        return migration.ImportProject(root, dryRun);
    }

    /// <summary>
    ///     Parses the repeatable <c>--projects</c> values into one selection. <c>all</c> and <c>none</c> are
    ///     keywords; any other value is a project path.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <c>none</c> is combined with another value, <c>all</c> is combined with a path, or a value is empty.
    ///     Each is client input, so the caller reports it as exit 2.
    /// </exception>
    internal static SquadProjectSelection ParseProjectSelection(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return new SquadProjectSelection(SquadProjectSelectionKind.Absent, []);

        List<string> tokens = [];
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException(
                    "--projects needs all, none, or a project path, and an empty value was given.");

            tokens.Add(trimmed);
        }

        bool hasNone = tokens.Any(token =>
            string.Equals(token, ProjectKeywordNone, StringComparison.OrdinalIgnoreCase));
        bool hasAll = tokens.Any(token => string.Equals(token, ProjectKeywordAll, StringComparison.OrdinalIgnoreCase));
        bool hasPath = tokens.Any(token =>
            !string.Equals(token, ProjectKeywordNone, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(token, ProjectKeywordAll, StringComparison.OrdinalIgnoreCase));

        if (hasNone && (hasAll || hasPath))
            throw new ArgumentException(
                "--projects none selects no project, so it cannot be combined with another --projects value. " +
                "Give none on its own, or give all or project path(s).");

        if (hasAll && hasPath)
            throw new ArgumentException(
                "--projects all selects every registered project, so it cannot be combined with a project path. " +
                "Give all on its own, or give project path(s).");

        if (hasNone) return new SquadProjectSelection(SquadProjectSelectionKind.None, []);

        if (hasAll) return new SquadProjectSelection(SquadProjectSelectionKind.All, []);

        return new SquadProjectSelection(
            SquadProjectSelectionKind.Paths,
            tokens.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    ///     Resolves each named project path to a registered project, or to an unregistered path that holds
    ///     in-repo state to import. Any other path is refused with the nearest registered path as a hint.
    /// </summary>
    /// <remarks>
    ///     Runs before any global work, so a typo fails the run with nothing written. The registry is matched
    ///     by canonical identity, so the operator can name a project through an alias or a link.
    /// </remarks>
    /// <exception cref="ArgumentException">A path is neither registered nor importable.</exception>
    internal static IReadOnlyList<string> ResolveNamedProjects(IReadOnlyList<string> paths, ISquadStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(stateStore);

        List<string> resolved = [];
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            SquadProjectRegistryEntry? entry = stateStore.FindProject(full);
            string chosen;
            if (entry is not null)
                chosen = entry.Path;
            else if (Directory.Exists(full) &&
                     File.Exists(Path.Combine(full, InRepoStateDirectoryName, ReceiptFileName)))
                chosen = full;
            else
                throw new ArgumentException(UnknownProjectMessage(full, stateStore.ListProjects()));

            if (!resolved.Contains(chosen, StringComparer.Ordinal)) resolved.Add(chosen);
        }

        return resolved;
    }

    /// <summary>Offers the operator a project to pick, through the injected picker or the Spectre picker.</summary>
    public static IReadOnlyList<string> RunProjectPicker(
        ISquadProjectPicker? picker,
        IReadOnlyList<SquadProjectRegistryEntry> projects)
    {
        return (picker ?? new SpectreSquadProjectPicker()).Pick(projects);
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

    /// <summary>Resolves the target root directory path.</summary>
    public static string ResolveTargetRoot(string? path)
    {
        return Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
    }

    /// <summary>Resolves the deployment scope from the global flag.</summary>
    public static SquadDeploymentScope ResolveScope(bool isGlobal)
    {
        return isGlobal ? SquadDeploymentScope.Global : SquadDeploymentScope.Project;
    }

}
