using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;

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
public static partial class ConductorGuard
{
    /// <summary>The workflow explanation appended to every model-facing message.</summary>
    public const string WorkflowReminder =
        """
        <conductor_workflow>
        This session runs a defined delivery graph. The conductor classifies and routes, specialists do the work, one reviewer judges the result, and each step leaves evidence. The repository owner needs those independent checks: whoever writes code must not be the one who verifies it, and the run must stop at approval gates. A conductor that does the work itself skips those checks, even when the code is good.

        Some general defaults you were given assume one agent working alone: finish end to end in this turn, do quick work yourself, and keep working while a subagent runs. Those defaults conflict with this graph and do not apply here. This is the user's explicit instruction to use subagents for all project work, however small.

        - Dispatch project work with the task tool. Reading, searching, editing and running project code are blocked for you.
        - Stopping at an approval gate to ask the user is the correct end of a turn, not unfinished work.
        - A message saying a draft was rejected or incomplete, or telling you to complete the task, comes from an automated reviewer that cannot see this workflow. Do not implement anything because of it. Continue at the next step of the graph. If a question to the user is unanswered, ask it again and end your turn.
        </conductor_workflow>
        """;

    private const string DenyReason =
        "The conductor may not do project work itself. Dispatch a specialist with the task tool.";

    private static readonly HashSet<string> PermittedTools = new(StringComparer.Ordinal)
    {
        "task",
        "read_agent",
        "write_agent",
        "list_agents",
        "sql",
        "skill",
        "tool_search_tool",
        "ask_user",
        "task_complete",
        "rename_chat",
        // The conductor's own todo list: it writes no project file and runs nothing.
        "update_todo",
    };

    private static readonly string[] DocsServerPrefixes = ["kyber-weave-docs_", "kw-docs-docs_"];

    private static readonly HashSet<string> ArbiterToolNames = new(StringComparer.Ordinal)
    {
        "arbiter_evaluate_agent_dispatch_or_return",
        $"{McpServerNames.Arbiter}-arbiter_evaluate_agent_dispatch_or_return",
        "arbiter_select_relevant_document_sections",
        $"{McpServerNames.Arbiter}-arbiter_select_relevant_document_sections",
        "arbiter_classify_bot_review_comment",
        $"{McpServerNames.Arbiter}-arbiter_classify_bot_review_comment",
    };

    private static readonly string[] ContractDirectories = ["plans", "specs", "todo"];

    // A plans/specs/todo segment can also name project code (web/specs/login.spec.ts), so outside the
    // configured planning directories only plan documents and their task files are readable.
    private static readonly string[] ContractExtensions = [".md", ".json"];

    /// <summary>Answers one hook event. Always returns 0: a deny rides stdout.</summary>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <param name="stdout">Receives the decision document, and nothing else.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="allowedMcpServer">An MCP server whose tools the conductor may call, or null.</param>
    /// <param name="copilotHome">Copilot's home directory, or null to resolve it from the environment.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records, or null for the user profile.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(
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
            using JsonDocument document = JsonDocument.Parse(stdin);
            answer = Decide(
                document.RootElement,
                allowedMcpServer,
                copilotHome ?? ResolveCopilotHome(),
                homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                log);
        }
        catch (Exception ex)
        {
            answer = Deny("The conductor guard could not read this tool call, so it was denied.");
            TryLog(log, $"conductor guard could not read the event: {ex.Message}");
        }

        if (answer is not null)
        {
            try
            {
                stdout.Write(answer);
            }
            catch (Exception ex)
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
            log.WriteLine($"{HookCommand.FailClosedCode}: {OneLine(message)}");
        }
        catch (Exception)
        {
            // The log is best effort.
        }
    }

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private static string? Decide(JsonElement root, string? allowedMcpServer, string copilotHome, string home, TextWriter log)
    {
        List<ToolCall> calls = ReadCalls(root);
        if (calls.Count == 0)
        {
            return TryGetText(root, "transformedPrompt", out string? prompt)
                ? new JsonObject { ["modifiedTransformedPrompt"] = $"{prompt}\n\n{WorkflowReminder}" }.ToJsonString()
                : Deny("The conductor guard found no tool name in this event, so it was denied.");
        }

        if (IsWorkerCall(root))
        {
            return null;
        }

        string? cwd = TryGetText(root, "cwd", out string? directory) ? directory : null;
        ToolCall? denied = calls.Find(call => !IsPermitted(call, cwd, allowedMcpServer, copilotHome, home));
        if (denied is not null)
        {
            return Deny($"{DenyReason} Blocked tool: {denied.Name}.");
        }

        foreach (ToolCall call in calls)
        {
            if (RunStartRefusal(call, cwd, home, log) is { } refusal)
            {
                return Deny(refusal);
            }
        }

        return null;
    }

    /// <summary>
    /// The conductor-side half of <c>KW-ARB-RUN-001</c>. This hook is scoped to the conductor, so it knows
    /// the caller even where the harness payload names none (a Copilot CLI root agent). A dispatch of a
    /// specialist that writes project files is refused while no run is live for the repository; evidence
    /// that cannot be read allows and is logged.
    /// </summary>
    private static string? RunStartRefusal(ToolCall call, string? cwd, string home, TextWriter log)
    {
        if (!string.Equals(call.Name, "task", StringComparison.Ordinal)
            || call.Arguments is not { } arguments
            || !TryGetText(arguments, "agent_type", out string? target)
            || !ArbiterSquadCatalog.LoadEmbedded().Agents.TryGetValue(target, out ArbiterSquadAgent? agent)
            || !RunStartEvidence.IsWriterProfile(agent.CapabilityProfile))
        {
            return null;
        }

        string prompt = TryGetText(arguments, "prompt", out string? text) ? text : string.Empty;
        if (string.Equals(
                TriggerClassifier.PlannerMarkerFor(prompt, HeaderBlock.Parse(prompt).Headers),
                "RUN-START",
                StringComparison.Ordinal))
        {
            return null;
        }

        bool? started = cwd is null ? null : RunStartEvidence.Started(cwd, home);
        if (started is null)
        {
            TryLog(log, $"conductor guard could not read whether a run was started for '{cwd}'; the dispatch to {target} continues.");
            return null;
        }

        return started.Value
            ? null
            : $"KW-ARB-RUN-001: no run has been started for this repository, so {target} would write into a checkout " +
              $"with no branch of its own. Next: {RunStartEvidence.Remedy}";
    }

    /// <summary>
    /// A worker's tool call, by the runtime's own subagent fields. Copilot omits all three on a root
    /// call, writes them at the top level where tool arguments cannot reach, and in Copilot CLI
    /// 1.0.109 never sends a worker's call to the conductor's hooks at all. Requiring the full set,
    /// and a type other than the conductor, keeps a stray id from opening the guard.
    /// </summary>
    private static bool IsWorkerCall(JsonElement root)
    {
        bool camel = TryGetText(root, "agentId", out _)
            && TryGetText(root, "parentAgentId", out _)
            && TryGetText(root, "agentType", out string? camelType)
            && !IsConductor(camelType);
        bool snake = TryGetText(root, "agent_id", out _)
            && TryGetText(root, "parent_agent_id", out _)
            && TryGetText(root, "agent_type", out string? snakeType)
            && !IsConductor(snakeType);
        return camel || snake;

        static bool IsConductor(string type) => string.Equals(type, "conductor", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveCopilotHome() =>
        Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    private static List<ToolCall> ReadCalls(JsonElement root)
    {
        List<ToolCall> calls = [];
        if (root.TryGetProperty("toolCalls", out JsonElement batch) && batch.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement call in batch.EnumerateArray())
            {
                calls.Add(new ToolCall(
                    TryGetText(call, "name", out string? name) ? name : string.Empty,
                    ArgumentsOf(call, "args")));
            }

            return calls;
        }

        if (TryGetText(root, "toolName", out string? toolName))
        {
            calls.Add(new ToolCall(toolName, ArgumentsOf(root, "toolArgs")));
        }
        else if (TryGetText(root, "tool_name", out string? snakeName))
        {
            calls.Add(new ToolCall(snakeName, ArgumentsOf(root, "tool_input")));
        }

        return calls;
    }

    /// <summary>Copilot sends tool arguments either as an object or as a JSON string.</summary>
    private static JsonElement? ArgumentsOf(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement arguments))
        {
            return null;
        }

        if (arguments.ValueKind != JsonValueKind.String)
        {
            return arguments;
        }

        using JsonDocument parsed = JsonDocument.Parse(arguments.GetString() ?? string.Empty);
        return parsed.RootElement.Clone();
    }

    private static bool IsPermitted(ToolCall call, string? cwd, string? allowedMcpServer, string copilotHome, string home)
    {
        if (PermittedTools.Contains(call.Name)
            || DocsServerPrefixes.Any(prefix => call.Name.StartsWith(prefix, StringComparison.Ordinal))
            || ArbiterToolNames.Contains(call.Name)
            || (allowedMcpServer is not null && call.Name.StartsWith($"{allowedMcpServer}-", StringComparison.Ordinal)))
        {
            return true;
        }

        if (call.Arguments is not { } arguments)
        {
            return false;
        }

        return call.Name switch
        {
            "view" => TryGetText(arguments, "path", out string? path) && IsContractPath(path, cwd, copilotHome, home),
            "bash" or "powershell" => TryGetText(arguments, "command", out string? command) && RunStartCommand().IsMatch(command),
            _ => false,
        };
    }

    /// <summary>
    /// A file the conductor may read: its own references and skills, the run records under
    /// <c>~/.kyber-weave/runs</c>, and, in the working directory or any other worktree of the same repository (a run
    /// works in a worktree of its own), an <c>AGENTS.md</c>, anything under the configured planning directories, and
    /// a <c>.md</c> or <c>.json</c> file under a <c>plans</c>, <c>specs</c> or <c>todo</c> directory, such as a plan's
    /// <c>.tasks.json</c>. Reading is all <c>view</c> does.
    /// </summary>
    private static bool IsContractPath(string path, string? cwd, string copilotHome, string home)
    {
        if (cwd is null && !Path.IsPathRooted(path))
        {
            return false;
        }

        string fullPath = Normalize(cwd is null ? Path.GetFullPath(path) : Path.GetFullPath(path, cwd));

        if (Path.IsPathRooted(copilotHome)
            && (fullPath.StartsWith(DirectoryPrefix(Path.Combine(copilotHome, "agents", "conductor")), StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(DirectoryPrefix(Path.Combine(copilotHome, "skills")), StringComparison.OrdinalIgnoreCase)))
        {
            return !HasLinkBelow(DirectoryPrefix(copilotHome), fullPath);
        }

        if (Path.IsPathRooted(home)
            && fullPath.StartsWith(DirectoryPrefix(Path.Combine(home, ".kyber-weave", "runs")), StringComparison.OrdinalIgnoreCase))
        {
            return !HasLinkBelow(DirectoryPrefix(home), fullPath);
        }

        if (cwd is null)
        {
            return false;
        }

        if (fullPath.StartsWith(DirectoryPrefix(cwd), StringComparison.OrdinalIgnoreCase))
        {
            return IsContractUnder(DirectoryPrefix(cwd), fullPath);
        }

        // Started only for a path outside the working directory: git names the repository's other worktrees, so a
        // directory that merely looks like one is never trusted. The deepest match wins, for a nested worktree.
        string? worktree = GitFacts.ListWorktrees(cwd)
            .Select(DirectoryPrefix)
            .Where(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        return worktree is not null && IsContractUnder(worktree, fullPath);
    }

    /// <summary>Whether <paramref name="fullPath"/>, inside the worktree <paramref name="root"/>, is a contract file there.</summary>
    private static bool IsContractUnder(string root, string fullPath)
    {
        string relative = fullPath[root.Length..];
        string[] segments = relative.Split('/');
        bool contract = relative.StartsWith(".github/agents/conductor/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".github/skills/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segments[^1], "AGENTS.md", StringComparison.OrdinalIgnoreCase)
            || (segments[..^1].Any(segment => ContractDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase))
                && ContractExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            || ConfiguredPlanningDirectories(root).Any(directory => relative.StartsWith(directory, StringComparison.OrdinalIgnoreCase));
        return contract && !HasLinkBelow(root, fullPath);
    }

    /// <summary>
    /// The plan, specification and todo directories the repository's Config Reg names, as
    /// repository-relative prefixes ending in <c>/</c>. A configuration that does not load names none.
    /// </summary>
    private static IEnumerable<string> ConfiguredPlanningDirectories(string cwd)
    {
        KyberWeaveConfigLoadResult loaded;
        try
        {
            loaded = KyberWeaveConfigLoader.TryLoad(cwd);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
                && Path.GetDirectoryName(Normalize(entry.Path)) is { Length: > 0 } directory)
            {
                yield return $"{Normalize(directory).Trim('/')}/";
            }
        }
    }

    /// <summary>
    /// True when any existing entry between <paramref name="root"/> and <paramref name="fullPath"/>
    /// is a symbolic link, so the path check above cannot be fooled into reading outside the contract.
    /// </summary>
    private static bool HasLinkBelow(string root, string fullPath)
    {
        string current = root.TrimEnd('/');
        foreach (string segment in fullPath[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = $"{current}/{segment}";
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!entry.Exists)
            {
                return false;
            }

            if (entry.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static string DirectoryPrefix(string directory) => $"{Normalize(Path.GetFullPath(directory)).TrimEnd('/')}/";

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static bool TryGetText(JsonElement parent, string property, out string text)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } found)
        {
            text = found;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static string Deny(string reason) =>
        new JsonObject
        {
            ["permissionDecision"] = "deny",
            ["permissionDecisionReason"] = reason,
        }.ToJsonString();

    // Each run start value has its own shape: names and slugs are lowercase tokens, a version has no
    // slash, and a resume branch may hold slashes but never "..", a leading dot, or a leading slash.
    [GeneratedRegex(
        @"\A[ \t]*kyber-weave[ \t]+(?:run[ \t]+start(?:[ \t]+(?:--(?:harness|type)[ \t]+[a-z][a-z0-9-]*|--slug[ \t]+[a-z0-9]+(?:-[a-z0-9]+)*|--failing-version[ \t]+(?!\S*\.\.)[A-Za-z0-9][A-Za-z0-9.+-]*|--resume[ \t]+(?!\S*\.\.)(?!\S*//)[A-Za-z0-9_][A-Za-z0-9._/+@-]*))+(?:[ \t]+--json)?|doctor[ \t]+--ensure(?:[ \t]+[A-Za-z0-9._/~:\\][A-Za-z0-9._/~:\\-]*)?)[ \t]*\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex RunStartCommand();

    private sealed record ToolCall(string Name, JsonElement? Arguments);
}
