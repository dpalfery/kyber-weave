using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
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

    /// <summary>Resolves the target root directory path.</summary>
    public static string ResolveTargetRoot(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);

    /// <summary>Resolves the deployment scope from the global flag.</summary>
    public static SquadDeploymentScope ResolveScope(bool isGlobal) =>
        isGlobal ? SquadDeploymentScope.Global : SquadDeploymentScope.Project;
}
