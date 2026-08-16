using System.Diagnostics.CodeAnalysis;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Release;
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

    /// <summary>Resolves the APM and MCP process probes using the specified process executor.</summary>
    public static void ResolveProbes(
        IProcessExecutor? executor,
        out ApmProcessProbe apmProbe,
        out McpProcessProbe mcpProbe)
    {
        IProcessExecutor resolvedExecutor = executor ?? ProcessExecutor.Instance;
        apmProbe = new ApmProcessProbe(resolvedExecutor);
        mcpProbe = new McpProcessProbe(resolvedExecutor);
    }

    /// <summary>Resolves the APM and MCP process probes using the default process executor.</summary>
    public static void ResolveProbes(
        out ApmProcessProbe apmProbe,
        out McpProcessProbe mcpProbe) =>
        ResolveProbes(null, out apmProbe, out mcpProbe);

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
        IProcessExecutor? executor = null,
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadReleaseSource? releaseSource = null,
        IApmRunner? apmRunner = null,
        ISquadTransactionObserver? observer = null,
        TimeProvider? timeProvider = null)
    {
        IProcessExecutor resolvedExecutor = executor ?? ProcessExecutor.Instance;
        SquadStateStore resolvedStateStore = stateStore ?? ResolveStateStore(userPaths);
        ISquadReleaseSource resolvedReleaseSource = releaseSource ?? new GitHubSquadReleaseSource(new Uri("https://api.github.com"));
        IApmRunner resolvedApmRunner = apmRunner ?? new ApmProcessRunner(resolvedExecutor);

        return new SquadLifecycleService(
            releaseSource: resolvedReleaseSource,
            apmRunner: resolvedApmRunner,
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
