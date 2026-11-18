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

    /// <summary>Resolves the Kyber-Weave MCP process probe using the specified process executor.</summary>
    public static McpProcessProbe ResolveProbe(IProcessExecutor? executor, IProcessEnvironment environment)
    {
        return new McpProcessProbe(executor ?? ProcessExecutor.Instance, environment);
    }

    /// <summary>Resolves the Kyber-Weave Arbiter process probe using the specified process executor.</summary>
    public static ArbiterProcessProbe ResolveArbiterProbe(IProcessExecutor? executor, IProcessEnvironment environment) =>
        new(executor ?? ProcessExecutor.Instance, environment);

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

    /// <summary>
    ///     True when a dry run found legacy state that a real run would migrate. The verb reports that and
    ///     stops, so it never reads a deployment the migration has not yet moved.
    /// </summary>
    public static bool IsPendingDryRunMigration(SquadMigrationReport report, bool dryRun)
    {
        return dryRun && (report.MigratedItems.Count > 0 || report.ImportedProjects.Count > 0);
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

    /// <summary>Offers the operator a project to pick, through the injected picker or the Spectre picker on the console.</summary>
    public static IReadOnlyList<string> RunProjectPicker(
        ISquadProjectPicker? picker,
        IReadOnlyList<SquadProjectRegistryEntry> projects,
        IAnsiConsole console)
    {
        return (picker ?? new SpectreSquadProjectPicker(console)).Pick(projects);
    }

    /// <summary>Resolves the deployment scope from the global flag.</summary>
    public static SquadDeploymentScope ResolveScope(bool isGlobal)
    {
        return isGlobal ? SquadDeploymentScope.Global : SquadDeploymentScope.Project;
    }

}
