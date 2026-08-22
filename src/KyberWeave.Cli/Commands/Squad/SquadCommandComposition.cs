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

    /// <summary>Resolves a state store using the specified or default user paths.</summary>
    public static SquadStateStore ResolveStateStore(ISquadUserPaths? userPaths = null) =>
        new(userPaths ?? SquadUserPaths.Instance);

    /// <summary>Resolves the Kyber-Weave MCP process probe using the specified process executor.</summary>
    public static McpProcessProbe ResolveProbe(IProcessExecutor? executor) =>
        new(executor ?? ProcessExecutor.Instance);

    /// <summary>Resolves the Kyber-Weave MCP process probe using the default process executor.</summary>
    public static McpProcessProbe ResolveProbe() => ResolveProbe(null);

    /// <summary>
    /// Resolves the renderer used to lower canonical Squad source into harness-native
    /// files. Copilot and Cursor are native; Antigravity is fallback role-skill lowering
    /// to <c>.agents/skills/</c>. Every other approved target fails closed with a pointer
    /// to its <c>docs/todo/</c> entry rather than being silently dropped from the roster.
    /// </summary>
    public static ISquadRenderer ResolveRenderer() =>
        new SquadRendererRegistry([new CopilotRenderer(), new CursorRenderer(), new AntigravityRenderer()]);

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
        TimeProvider? timeProvider = null)
    {
        SquadStateStore resolvedStateStore = stateStore ?? ResolveStateStore(userPaths);
        ISquadReleaseSource resolvedReleaseSource = releaseSource
            ?? new GitHubSquadReleaseSource(ReleaseOrigin.Resolve(Environment.GetEnvironmentVariable).ApiRoot);
        ISquadRenderer resolvedRenderer = renderer ?? ResolveRenderer();

        return new SquadLifecycleService(
            releaseSource: resolvedReleaseSource,
            renderer: resolvedRenderer,
            stateStore: resolvedStateStore,
            timeProvider: timeProvider,
            observer: observer);
    }

    /// <summary>Resolves the target root directory path.</summary>
    public static string ResolveTargetRoot(string? path) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);

    /// <summary>Resolves the deployment scope from the global flag.</summary>
    public static SquadDeploymentScope ResolveScope(bool isGlobal) =>
        isGlobal ? SquadDeploymentScope.Global : SquadDeploymentScope.Project;
}
