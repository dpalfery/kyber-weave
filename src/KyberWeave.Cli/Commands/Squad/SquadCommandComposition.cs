using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Rendering;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// CLI composition root for Squad commands: provides factory and resolution methods
/// to resolve roots, scope, state stores, transactions, process probes, and console context.
/// </summary>
internal static class SquadCommandComposition
{
    /// <summary>Indicates whether the console environment supports interactive input.</summary>
    public static bool IsInteractiveConsole() =>
        !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive;

    /// <summary>
    /// Writes the exit-2 client-input error line without console folding. The hint names
    /// the operator's paths verbatim, and a redirected console folds at 80 columns,
    /// splitting a long path across lines — corrupting the one thing the hint exists
    /// for: a copy-pastable name for each of the conflicting forms.
    /// </summary>
    public static void WriteClientInputError(string message) =>
        WriteUnfolded(() => AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(message)}[/]"));

    /// <summary>
    /// Runs <paramref name="write"/> with console folding lifted, then restores the width.
    /// </summary>
    /// <remarks>
    /// A console with no detectable width — no TTY, as in a container or a CI shell — reports
    /// 0 from <c>Profile.Width</c>, and Spectre's setter rejects 0. Restoring it verbatim threw
    /// from the <c>finally</c> and replaced the command's own output and exit code with an
    /// unhandled exception, so such a console is restored to <see cref="ConsoleWidth.Default"/>.
    /// </remarks>
    internal static void WriteUnfolded(Action write)
    {
        int originalWidth = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;
        try
        {
            write();
        }
        finally
        {
            AnsiConsole.Profile.Width = originalWidth > 0 ? originalWidth : ConsoleWidth.Default;
        }
    }

    /// <summary>Resolves the state store using the specified or default user paths.</summary>
    public static SquadStateStore ResolveStateStore(ISquadUserPaths? userPaths = null) =>
        new(userPaths ?? SquadUserPaths.Instance);

    /// <summary>
    /// Resolves the per-target global deployment root resolver. Commands that read deployed
    /// files back — status, uninstall dry-runs — need the same roots install resolved,
    /// because a global receipt's relative paths are only meaningful beneath each target's
    /// own root.
    /// </summary>
    public static ISquadGlobalRootResolver ResolveGlobalRoots() => new SquadGlobalRoots(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ReadFileTextOrNull);

    /// <summary>
    /// Resolves each selected target's physical global root — the directory a
    /// <c>--global</c> lifecycle actually writes — through the same resolver (and
    /// therefore the same override environment variables, <c>CODEX_HOME</c>,
    /// <c>CLAUDE_CONFIG_DIR</c>, …) the lifecycle's plan resolves with moments later.
    /// </summary>
    /// <remarks>
    /// A target whose root cannot be resolved — no verified per-user directory, or an
    /// override naming a relative path — is skipped rather than thrown: the confirmation
    /// gate must still name the roots that would genuinely change, and the lifecycle call
    /// after it remains the step that reports an unresolvable root as a failure.
    /// </remarks>
    public static IReadOnlyList<(SquadTarget Target, string GlobalRoot)> ResolveGlobalTargetRoots(
        IReadOnlyList<SquadTarget> targets)
    {
        ISquadGlobalRootResolver globalRoots = ResolveGlobalRoots();
        List<(SquadTarget Target, string GlobalRoot)> resolved = [];
        foreach (SquadTarget target in targets)
        {
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
        }

        return resolved;
    }

    /// <summary>
    /// Derives the per-target global roots a <c>--global</c> uninstall will write from the
    /// deployment receipt: uninstall has no <c>--target</c> option, so the receipt's owned
    /// files are the authoritative target list, and the lifecycle resolves each through
    /// <see cref="ISquadGlobalRootResolver.ResolveGlobalRoot"/> at plan time.
    /// </summary>
    /// <remarks>
    /// Every failure mode degrades to "no per-target roots to name" rather than throwing:
    /// an absent receipt means nothing will be uninstalled, and a corrupt receipt or an
    /// unknown target token is surfaced by the lifecycle call after the gate, which reads
    /// the same state and fails under the command's exit-1 convention.
    /// </remarks>
    public static IReadOnlyList<(SquadTarget Target, string GlobalRoot)> ResolveUninstallGlobalTargetRoots(
        SquadStateStore stateStore,
        string targetRoot)
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

        if (receipt is null)
        {
            return [];
        }

        IReadOnlyList<SquadTarget> targets;
        try
        {
            targets = SquadTargetCatalog.Parse(receipt.Files.Select(file => file.Target).Distinct());
        }
        catch (ArgumentException)
        {
            return [];
        }

        return ResolveGlobalTargetRoots(targets);
    }

    /// <summary>
    /// The file-reading port <see cref="SquadGlobalRoots"/> uses for the one target whose
    /// global root can be set in a config file rather than an environment variable. Absent or
    /// unreadable is not an error: the resolver falls back to that target's default, matching
    /// what the harness itself does with the same file.
    /// </summary>
    internal static string? ReadFileTextOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Resolves the Kyber-Weave MCP process probe using the specified process executor.</summary>
    public static McpProcessProbe ResolveProbe(IProcessExecutor? executor) =>
        new(executor ?? ProcessExecutor.Instance);

    /// <summary>Resolves the Kyber-Weave MCP process probe using the default process executor.</summary>
    public static McpProcessProbe ResolveProbe() => ResolveProbe(null);

    /// <summary>
    /// Resolves the renderer used to lower canonical Squad source into harness-native
    /// files. Copilot, Cursor, Claude, Codex, OpenCode, Kilo, Pi, Factory, and Antigravity are native;
    /// Warp is fallback role-skill lowering (to <c>.warp/skills/</c>). Antigravity is native via
    /// per-agent directories at <c>.agents/agents/{name}/agent.md</c> with canonical skills at
    /// <c>.agents/skills/{name}/SKILL.md</c> (the "Native Both" pattern). Pi is native through the
    /// third-party <c>@tintinweb/pi-subagents</c> extension's custom-agent format, with its one
    /// <c>invocation: primary</c> agent lowered to a top-level skill because Pi core has no
    /// primary-agent primitive (see <see cref="PiRenderer"/> remarks).
    /// Every other approved target fails closed with a pointer to its <c>docs/todo/</c>
    /// entry rather than being silently dropped from the roster.
    /// </summary>
    public static ISquadRenderer ResolveRenderer() =>
        new SquadRendererRegistry(
        [
            new CopilotRenderer(),
            new CursorRenderer(),
            new ClaudeRenderer(),
            new AntigravityRenderer(),
            new CodexRenderer(),
            new OpenCodeRenderer(),
            new KiloRenderer(),
            new PiRenderer(),
            new FactoryRenderer(),
            new WarpRenderer(),
            new ZCodeRenderer(),
        ]);

    /// <summary>Resolves a deployment transaction using the specified or default state store.</summary>
    public static SquadTransaction ResolveTransaction(
        SquadStateStore? stateStore = null,
        ISquadUserPaths? userPaths = null,
        ISquadTransactionObserver? observer = null) =>
        new(stateStore ?? ResolveStateStore(userPaths), observer);

    /// <summary>Creates a Squad lifecycle service using injected or default collaborators.</summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Collaborator lifecycles are managed across the command execution.")]
    public static SquadLifecycleService CreateLifecycleService(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        ISquadRenderer? renderer = null,
        ISquadTransactionObserver? observer = null,
        TimeProvider? timeProvider = null,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        SquadStateStore resolvedStateStore = stateStore ?? ResolveStateStore(userPaths);
        ISquadReleaseSource resolvedReleaseSource = releaseSource
            ?? new GitHubSquadReleaseSource(ReleaseOrigin.Resolve(Environment.GetEnvironmentVariable).ApiRoot);
        ISquadRenderer resolvedRenderer = renderer ?? ResolveRenderer();
        ISquadGlobalRootResolver resolvedGlobalRoots = globalRoots ?? ResolveGlobalRoots();

        return new SquadLifecycleService(
            releaseSource: resolvedReleaseSource,
            renderer: resolvedRenderer,
            stateStore: resolvedStateStore,
            timeProvider: timeProvider,
            observer: observer,
            globalRoots: resolvedGlobalRoots);
    }

    /// <summary>
    /// Coalesces the positional path argument with the <c>--path</c> option into the one
    /// effective target path. The option wins when the positional is absent or at its
    /// <c>"."</c> default; the positional passes through when no option was supplied;
    /// supplying both a non-default positional and the option is a conflict.
    /// </summary>
    /// <remarks>
    /// Spectre.Console.Cli cannot carry a <c>[CommandArgument]</c> and a
    /// <c>[CommandOption]</c> on one property, so <c>--path</c> binds a separate nullable
    /// <c>PathOption</c> and this is the single seam where the two forms meet. The conflict
    /// throws <see cref="ArgumentException"/> so it flows through the commands' existing
    /// client-input catch into the exit-2 convention, and its hint names both forms so the
    /// operator can pick one.
    /// </remarks>
    public static string? CoalesceTargetPath(string? positional, string? option)
    {
        if (string.IsNullOrWhiteSpace(option))
        {
            return positional;
        }

        bool positionalIsDefault =
            string.IsNullOrWhiteSpace(positional) || string.Equals(positional, ".", StringComparison.Ordinal);
        if (positionalIsDefault)
        {
            return option;
        }

        throw new ArgumentException(
            $"The deployment root was supplied twice: as the positional '{positional}' and as the option '--path {option}'. " +
            "Supply either the positional path or --path <PATH>, not both.");
    }

    /// <summary>
    /// Normalizes a pinned <c>-v|--version</c> value into the release version to request,
    /// or <c>null</c> when the flag is omitted so the lifecycle's default version
    /// resolution applies unchanged.
    /// </summary>
    /// <remarks>
    /// Two gates, in this order, hold every invalid pinned version on the commands'
    /// exit-2 client-input convention before root resolution and before any network call.
    /// <see cref="ReleaseVersion.Normalize"/> runs first because it is the looser,
    /// canonicalizing rule: it strips a leading 'v' and '+build' metadata (so
    /// 'v1.2.3+sha' becomes '1.2.3') and rejects input that is not a Release tag shape at
    /// all. The strict SemVer check runs second and reuses
    /// <see cref="GitHubSquadReleaseSource.IsValidReleaseVersion"/> — the one rule the
    /// release source itself enforces, reused rather than duplicated so the two cannot
    /// diverge — to reject tag-shaped forms the stricter rule still forbids, like '1.0'
    /// or '01.2.3'. The release source keeps its own check as defense-in-depth.
    /// </remarks>
    /// <exception cref="SelfUpdateException">The value is not a Release tag shape.</exception>
    /// <exception cref="ArgumentException">The value is tag-shaped but not strict SemVer.</exception>
    public static string? NormalizePinnedVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        string normalized = ReleaseVersion.Normalize(version);
        if (!GitHubSquadReleaseSource.IsValidReleaseVersion(normalized))
        {
            throw new ArgumentException(
                $"--version must be a semantic version (X.Y.Z or X.Y.Z-prerelease); " +
                "a leading 'v' and '+build' metadata are accepted and stripped. " +
                $"Got '{version}'.");
        }

        return normalized;
    }

    /// <summary>Resolves the target root directory path.</summary>
    public static string ResolveTargetRoot(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);

    /// <summary>Resolves the deployment scope from the global flag.</summary>
    public static SquadDeploymentScope ResolveScope(bool isGlobal) =>
        isGlobal ? SquadDeploymentScope.Global : SquadDeploymentScope.Project;
}
