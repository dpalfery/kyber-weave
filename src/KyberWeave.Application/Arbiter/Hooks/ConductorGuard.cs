using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The Copilot conductor's role boundary. VS Code limits a worker's built-in tools to those of
/// the selected agent, so the conductor must hold tools it may not use itself. This hook denies
/// them at run time and restates the workflow on every model-facing message.
/// </summary>
/// <remarks>
/// The hook is scoped to the conductor by the agent file, so it needs no configuration; it reads the
/// repository's, best effort, only for the planning directories its Config Reg names. It
/// writes nothing for a permitted tool, which leaves Copilot's normal permission flow in charge.
/// A deny is always explicit: Copilot runs the tool when a hook times out, prints nothing, or
/// prints invalid JSON, so an unreadable event is denied rather than ignored.
/// </remarks>
/// <param name="git">Names the repository's worktrees and its common directory.</param>
/// <param name="runStates">Tells whether a run is live for a repository.</param>
/// <param name="hostConfig">Loads the repository's configuration, for its planning directories.</param>
/// <param name="links">Finds symbolic links on a path the conductor is asked to read.</param>
/// <param name="process">The operator's home and Copilot's home variable.</param>
/// <param name="runContexts">Resolves the repository and its worktrees once per event; null reads them from the event's
/// own directory, which is the behaviour of a guard built without the resolver.</param>
public sealed class ConductorGuard(
    IGitRepositoryFacts git,
    IRunStateStore runStates,
    IHostConfigLoader hostConfig,
    IFileLinkProbe links,
    IProcessEnvironment process,
    IHookRunContextResolver? runContexts = null) : IConductorGuard
{
    /// <summary>The workflow explanation appended to every model-facing message.</summary>
    public const string WorkflowReminder = ConductorGuardRules.WorkflowReminder;

    /// <summary>The deny document the guard answers with: Copilot runs a tool when a hook prints nothing, so every refusal is explicit.</summary>
    /// <param name="reason">Why the call is denied.</param>
    /// <returns>The document for stdout.</returns>
    public static string DenyDocument(string reason) => ConductorGuardRules.Deny(reason);

    /// <inheritdoc/>
    public int Run(
        string stdin,
        TextWriter stdout,
        TextWriter log,
        string? allowedMcpServer = null,
        string? copilotHome = null,
        string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(log);

        string? answer;
        try
        {
            ConductorGuardEvent hookEvent = ConductorHookEventReader.Read(stdin);
            answer = Decide(
                hookEvent,
                allowedMcpServer,
                copilotHome ?? ResolveCopilotHome(),
                homeDirectory ?? process.HomeDirectory,
                log);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            // Hook entry point: nothing may crash the host, and the deny is logged.
            answer = ConductorGuardRules.Deny("The conductor guard could not read this tool call, so it was denied.");
            TryLog(log, $"conductor guard could not read the event: {ex.Message}");
        }

        return Respond(answer, stdout, log);
    }

    /// <summary>
    /// Answers the event when no file, git or configuration read can change the answer, so a process builds no container
    /// for it. Returns false when the guard must read, and when the event is unreadable, so the guard reports it.
    /// </summary>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <param name="stdout">Receives the decision document, and nothing else.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="allowedMcpServer">An MCP server whose tools the conductor may call, or null.</param>
    /// <param name="exitCode">The process exit code when the event was answered.</param>
    /// <returns>True when the event was answered.</returns>
    public static bool TryAnswer(string stdin, TextWriter stdout, TextWriter log, string? allowedMcpServer, out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(log);
        ConductorGuardEvent hookEvent;
        try
        {
            hookEvent = ConductorHookEventReader.Read(stdin);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            exitCode = 0;
            return false;
        }

        if (!ConductorGuardRules.TryDecideWithoutReads(hookEvent, allowedMcpServer, out string? answer))
        {
            exitCode = 0;
            return false;
        }

        exitCode = Respond(answer, stdout, log);
        return true;
    }

    private static int Respond(string? answer, TextWriter stdout, TextWriter log)
    {
        if (answer is not null)
        {
            try
            {
                stdout.Write(answer);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Nothing more can reach Copilot once stdout fails; the exit code stays 0 as documented.
                TryLog(log, $"conductor guard could not write its answer: {ex.Message}");
            }
        }

        return 0;
    }

    private static void TryLog(TextWriter log, string message)
    {
        try
        {
            log.WriteLine($"{HookCommand.FailClosedCode}: {ConductorGuardRules.OneLine(message)}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The log is best effort.
        }
    }

    private string? Decide(
        ConductorGuardEvent hookEvent,
        string? allowedMcpServer,
        string copilotHome,
        string home,
        TextWriter log)
    {
        if (hookEvent.Calls.Count == 0)
        {
            return hookEvent.TransformedPrompt is { } prompt
                ? new JsonObject { ["modifiedTransformedPrompt"] = $"{prompt}\n\n{ConductorGuardRules.WorkflowReminder}" }.ToJsonString()
                : ConductorGuardRules.Deny("The conductor guard found no tool name in this event, so it was denied.");
        }

        if (hookEvent.IsWorkerCall)
        {
            return null;
        }

        // The run context costs two git calls and a run-record read, and the guard runs on every conductor tool call.
        // Only a path the conductor may read, or a dispatch that must have a live run, needs it; a call decided by
        // its tool name is answered without reading anything, as it was before the run context existed.
        bool needsContext = hookEvent.Calls.Any(call =>
            string.Equals(call.Name, "view", StringComparison.Ordinal)
            || ConductorGuardRules.RunStartTargetOf(call) is not null);
        HookRunContext? context = needsContext ? ResolveContext(hookEvent.Cwd, home) : null;
        IReadOnlyList<string> roots = context?.Worktrees ?? [];
        string? cwd = hookEvent.Cwd;

        ConductorToolCall? denied = hookEvent.Calls.FirstOrDefault(
            call => !IsPermitted(call, cwd, roots, allowedMcpServer, copilotHome, home));
        if (denied is not null)
        {
            return ConductorGuardRules.Deny($"{ConductorGuardRules.DenyReason} Blocked tool: {denied.Name}.");
        }

        foreach (ConductorToolCall call in hookEvent.Calls)
        {
            if (RunStartRefusal(call, cwd, context, home, log) is { } refusal)
            {
                return ConductorGuardRules.Deny(refusal);
            }
        }

        return null;
    }

    /// <summary>
    /// The invocation's run context, so the repository and its worktrees are resolved once and the same way the
    /// dispatch hooks resolve them. A resolver that cannot answer leaves the event's own directory in charge.
    /// </summary>
    private HookRunContext? ResolveContext(string? cwd, string home)
    {
        if (runContexts is null)
        {
            return null;
        }

        try
        {
            return runContexts.Resolve(new HookRunContextRequest(
                HarnessRegistry.CopilotCliToken,
                WorkspaceRoot: null,
                EventCwd: cwd,
                ProcessDirectory: process.CurrentDirectory,
                AgentWorktree: null,
                Prompt: null,
                home));
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The conductor-side half of <c>KW-ARB-RUN-001</c>. This hook is scoped to the conductor, so it knows
    /// the caller even where the harness payload names none (a Copilot CLI root agent). A dispatch of a
    /// specialist that writes project files is refused while no run is live for the repository; evidence
    /// that cannot be read allows and is logged.
    /// </summary>
    private string? RunStartRefusal(
        ConductorToolCall call,
        string? cwd,
        HookRunContext? context,
        string home,
        TextWriter log)
    {
        if (ConductorGuardRules.RunStartTargetOf(call) is not { } target)
        {
            return null;
        }

        // A context that resolved no common directory means git could not name the repository. That is evidence the
        // guard could not read, so it allows and logs; it never falls back to the directory as if it were a repository.
        string? repository = context is null
            ? (cwd is null ? null : git.GetCommonDirectory(cwd))
            : context.CommonDirectory;
        bool? started = repository is null ? null : RunStarted(repository, home);
        if (started is null)
        {
            TryLog(log, $"conductor guard could not read whether a run was started for '{cwd}'; the dispatch to {target} continues.");
            return null;
        }

        return started.Value
            ? null
            : $"KW-ARB-RUN-001: no run has been started for this repository, so {target} would write into a checkout " +
              $"with no branch of its own. Next: {RunWriterProfiles.Remedy}";
    }

    /// <summary>Whether a run is live for the repository; null when git cannot name it or the records cannot be read.</summary>
    private bool? RunStarted(string repository, string home)
    {
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        return runStates.HasLiveRun(home, repository);
    }

    private string ResolveCopilotHome() =>
        process.GetVariable("COPILOT_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(process.HomeDirectory, ".copilot");

    private bool IsPermitted(
        ConductorToolCall call,
        string? cwd,
        IReadOnlyList<string> roots,
        string? allowedMcpServer,
        string copilotHome,
        string home)
    {
        if (ConductorGuardRules.IsPermittedByName(call, allowedMcpServer))
        {
            return true;
        }

        if (!call.HasArguments)
        {
            return false;
        }

        return call.Name switch
        {
            "view" => call.Path is { } path && IsContractPath(path, cwd, roots, copilotHome, home),
            "bash" or "powershell" => call.Command is { } command && ConductorGuardRules.RunStartCommand().IsMatch(command),
            _ => false,
        };
    }

    /// <summary>
    /// A file the conductor may read: its own references and skills, the run records under
    /// <c>~/.kyber-weave/runs</c>, and, in the working directory or any other worktree of the same repository (a run
    /// works in a worktree of its own), an <c>AGENTS.md</c>, anything under the configured planning directories, and
    /// a <c>.md</c> or <c>.json</c> file under a <c>plans</c>, <c>specs</c> or <c>todo</c> directory, such as a plan's
    /// <c>.tasks.yml</c>. Reading is all <c>view</c> does.
    /// </summary>
    private bool IsContractPath(
        string path,
        string? cwd,
        IReadOnlyList<string> roots,
        string copilotHome,
        string home)
    {
        if (cwd is null && !Path.IsPathRooted(path))
        {
            return false;
        }

        string fullPath = ConductorGuardRules.Normalize(Path.GetFullPath(path, cwd ?? process.CurrentDirectory));

        if (Path.IsPathRooted(copilotHome)
            && (fullPath.StartsWith(Prefix(Path.Combine(copilotHome, "agents", "conductor")), StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(Prefix(Path.Combine(copilotHome, "skills")), StringComparison.OrdinalIgnoreCase)))
        {
            return !links.HasLinkBelow(Prefix(copilotHome), fullPath);
        }

        if (Path.IsPathRooted(home)
            && fullPath.StartsWith(Prefix(UserStatePaths.RunsDirectory(home)), StringComparison.OrdinalIgnoreCase))
        {
            return !links.HasLinkBelow(Prefix(home), fullPath);
        }

        if (cwd is null)
        {
            return false;
        }

        if (fullPath.StartsWith(Prefix(cwd), StringComparison.OrdinalIgnoreCase))
        {
            return IsContractUnder(Prefix(cwd), fullPath);
        }

        // Started only for a path outside the working directory: the run context lists this repository's own
        // worktrees, so a directory that merely looks like one is never trusted. The deepest match wins.
        string? worktree = (roots.Count > 0 ? roots : ListWorktreesOrNone(cwd))
            .Select(Prefix)
            .Where(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        return worktree is not null && IsContractUnder(worktree, fullPath);
    }

    /// <summary>The directory's prefix, a relative one resolved against the process's current directory.</summary>
    private string Prefix(string directory) => ConductorGuardRules.DirectoryPrefix(directory, process.CurrentDirectory);

    /// <summary>The repository's worktrees, or none when git or the container cannot answer: the guard then trusts only the working directory.</summary>
    private IReadOnlyList<string> ListWorktreesOrNone(string cwd)
    {
        try
        {
            return git.ListWorktrees(cwd);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Whether <paramref name="fullPath"/>, inside the worktree <paramref name="root"/>, is a contract file there.</summary>
    private bool IsContractUnder(string root, string fullPath)
    {
        string relative = fullPath[root.Length..];
        return ConductorGuardRules.IsContractRelative(relative, ConfiguredPlanningDirectories(root))
            && !links.HasLinkBelow(root, fullPath);
    }

    /// <summary>
    /// The plan, specification and todo directories the repository's Config Reg names, as
    /// repository-relative prefixes ending in <c>/</c>. A configuration that does not load names none.
    /// </summary>
    private IEnumerable<string> ConfiguredPlanningDirectories(string cwd)
    {
        HostConfigLoadResult loaded;
        try
        {
            loaded = hostConfig.TryLoad(cwd);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            yield break;
        }

        if (!loaded.Success || loaded.Config is not { } config)
        {
            yield break;
        }

        foreach (ConfigRegEntry entry in config.ConfigReg.Resolve(config.Ontology))
        {
            if ((string.Equals(entry.Name, ConfigRegConfig.PlanIndexProperty, StringComparison.Ordinal)
                    || string.Equals(entry.Name, ConfigRegConfig.SpecificationIndexProperty, StringComparison.Ordinal)
                    || string.Equals(entry.Name, ConfigRegConfig.TodoIndexProperty, StringComparison.Ordinal))
                && Path.GetDirectoryName(ConductorGuardRules.Normalize(entry.Path)) is { Length: > 0 } directory)
            {
                yield return $"{ConductorGuardRules.Normalize(directory).Trim('/')}/";
            }
        }
    }
}
