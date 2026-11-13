using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The guard on a Squad subagent's own tool calls, from machine facts only. It refuses a write into the
/// repository's canonical checkout while a live run works in a worktree of its own
/// (<c>KW-ARB-CHECKOUT-001</c>), and it ends a developer dispatch that has run past its budget of tool calls
/// or minutes (<c>KW-ARB-BUDGET-001</c>).
/// </summary>
/// <remarks>
/// It runs on every tool call a harness reports, so it starts no process and calls no model: the run records,
/// the checkout's git files and one append-only counter file per subagent are all it reads. A root agent (a
/// person's own session, or the conductor) is never guarded, and neither is an agent outside the Squad
/// catalog. Any error allows, and the log says why (ADR 0072).
/// </remarks>
public static class WorkerGuard
{
    /// <summary>The rule id a stray write is refused under.</summary>
    public const string CheckoutRuleId = "KW-ARB-CHECKOUT-001";

    /// <summary>The rule id an exhausted budget is refused under.</summary>
    public const string BudgetRuleId = "KW-ARB-BUDGET-001";

    /// <summary>The status line a worker whose budget ran out returns.</summary>
    public const string BudgetExceededStatus = "STATUS: BUDGET_EXCEEDED";

    /// <summary>The Copilot CLI harness token.</summary>
    public const string CopilotCliHarness = "copilot-cli";

    /// <summary>The Claude Code harness token.</summary>
    public const string ClaudeHarness = "claude";

    private const string BudgetDirectoryName = "budgets";
    private static readonly TimeSpan BudgetRetention = TimeSpan.FromDays(2);

    /// <summary>The capability profiles whose dispatches carry a budget: the developers and docs-dev.</summary>
    private static readonly HashSet<string> BudgetedProfiles = new(StringComparer.Ordinal) { "worker", "documentation" };

    /// <summary>Answers one tool-call event. Always returns 0: a deny rides stdout.</summary>
    /// <param name="harness">The harness token: <c>copilot-cli</c> or <c>claude</c>.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <param name="stdout">Receives the deny document, and nothing for an allow.</param>
    /// <param name="log">Receives diagnostics.</param>
    /// <param name="homeDirectory">The operator's home, or null for the user profile.</param>
    /// <param name="now">The clock, or null for the system clock.</param>
    /// <param name="budgetLoader">Reads the budget for a working directory, or null to read its <c>kyber-weave.yml</c>.</param>
    public static int Run(
        string harness,
        string stdin,
        TextWriter stdout,
        TextWriter log,
        string? homeDirectory = null,
        Func<DateTimeOffset>? now = null,
        Func<string, ArbiterDispatchBudgetConfig?>? budgetLoader = null)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            string home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ToolCall? call = Read(harness, stdin);
            if (call is null)
            {
                return 0;
            }

            string? reason = BudgetRefusal(call, home, (now ?? (() => DateTimeOffset.UtcNow))(), budgetLoader ?? LoadBudget)
                             ?? WriteRefusal(call, home);
            if (reason is not null)
            {
                stdout.Write(Deny(harness, reason));
            }
        }
        catch (Exception ex)
        {
            TryLog(log, $"{HookCommand.FailClosedCode}: worker guard could not decide ({OneLine(ex.Message)}); the tool call continues.");
        }

        return 0;
    }

    /// <summary>
    /// The stray-write refusal, unless the project turns the Arbiter off. The configuration is read only when
    /// there is something to refuse, so an ordinary write costs no YAML parse.
    /// </summary>
    private static string? WriteRefusal(ToolCall call, string home)
    {
        string? refusal = call.WritePath is null
            ? null
            : RunCheckout.StrayWriteRefusal(call.WritePath, call.Cwd ?? string.Empty, home);
        return refusal is not null && ArbiterTurnedOff(call.Cwd) ? null : refusal;
    }

    private static bool ArbiterTurnedOff(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return false;
        }

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(workingDirectory);
        return loaded.Success && loaded.Config?.Arbiter.ExplicitlyEnabled == false;
    }

    /// <summary>
    /// Counts this call against the subagent's budget and refuses it once the count or the elapsed time is past
    /// the limit. The first call writes the header (start time and limits); every call appends one timestamped
    /// line, so parallel hook processes never lose a count.
    /// </summary>
    /// <remarks>
    /// A harness may resume the same subagent for a rework, under the same id. A window therefore ends when the
    /// subagent has been idle longer than the budget's minutes, or for a minute after it ran past its budget
    /// (the worker returned): the resumed dispatch starts a fresh window.
    /// </remarks>
    private static string? BudgetRefusal(
        ToolCall call,
        string home,
        DateTimeOffset now,
        Func<string, ArbiterDispatchBudgetConfig?> budgetLoader)
    {
        if (!BudgetedProfiles.Contains(call.Agent.CapabilityProfile))
        {
            return null;
        }

        string directory = BudgetDirectory(home);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, KeyOf(call.Harness, call.AgentId) + ".log");
        if (File.Exists(path) && WindowEnded(File.ReadAllLines(path), now))
        {
            File.Delete(path);
        }

        if (!File.Exists(path))
        {
            ArbiterDispatchBudgetConfig? budget = budgetLoader(call.Cwd ?? string.Empty);
            if (budget is null)
            {
                return null;
            }

            Sweep(directory, now);
            try
            {
                using FileStream created = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
                byte[] headerBytes = Encoding.UTF8.GetBytes(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{now.ToUnixTimeMilliseconds()} {budget.Minutes} {budget.ToolCalls} {call.Agent.Name}\n"));
                created.Write(headerBytes);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another hook process wrote the header first; its limits stand.
            }
        }

        using (FileStream append = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            append.Write(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{now.ToUnixTimeMilliseconds()}\n")));
        }

        string[] lines = File.ReadAllLines(path);
        if (Window.Read(lines) is not { } window)
        {
            return null;
        }

        int calls = window.Calls;
        int minutes = window.Minutes;
        int maxCalls = window.MaxCalls;
        TimeSpan elapsed = now - window.Start;
        if (!window.IsOver(now))
        {
            return null;
        }

        return $"{BudgetRuleId}: this dispatch has made {calls} tool calls in {(int)elapsed.TotalMinutes} minutes, past its " +
               $"budget of {maxCalls} calls or {minutes} minutes. Stop now: make no more tool calls. Return a report whose " +
               $"first line is {BudgetExceededStatus}, then what is done, what is not, and the last failure you saw.";
    }

    private static bool WindowEnded(string[] lines, DateTimeOffset now)
    {
        if (Window.Read(lines) is not { } window)
        {
            // An empty file is one another hook process is still creating; anything else unreadable starts over.
            return lines.Length > 0;
        }

        TimeSpan idle = now - window.LastCall;
        return idle > TimeSpan.FromMinutes(window.Minutes) || (window.IsOver(window.LastCall) && idle > TimeSpan.FromMinutes(1));
    }

    private static ToolCall? Read(string harness, string stdin)
    {
        using JsonDocument document = JsonDocument.Parse(stdin);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? cwd = Text(root, "cwd");
        if (string.Equals(harness, CopilotCliHarness, StringComparison.Ordinal))
        {
            // A subagent's call carries the runtime's subagent fields at the top level; a root call carries none.
            string? agentId = Text(root, "agentId") ?? Text(root, "agent_id");
            string? parent = Text(root, "parentAgentId") ?? Text(root, "parent_agent_id");
            string? type = Text(root, "agentType") ?? Text(root, "agent_type");
            string? tool = Text(root, "toolName");
            if (agentId is null || parent is null || SquadAgentOf(type) is not { } agent || tool is null)
            {
                return null;
            }

            JsonObject? args = ArgumentsOf(root, "toolArgs");
            string? path = tool is "edit" or "create" && args is not null ? NodeText(args, "path") : null;
            return new ToolCall(harness, agentId, agent, cwd, path);
        }

        if (string.Equals(harness, ClaudeHarness, StringComparison.Ordinal))
        {
            // Claude adds agent_id and agent_type only to a call made inside a subagent.
            string? agentId = Text(root, "agent_id");
            string? tool = Text(root, "tool_name");
            if (agentId is null || SquadAgentOf(Text(root, "agent_type")) is not { } agent || tool is null)
            {
                return null;
            }

            JsonObject? input = ArgumentsOf(root, "tool_input");
            string? path = input is null
                ? null
                : tool switch
                {
                    "Edit" or "Write" or "MultiEdit" => NodeText(input, "file_path"),
                    "NotebookEdit" => NodeText(input, "notebook_path"),
                    _ => null,
                };
            return new ToolCall(harness, agentId, agent, cwd, path);
        }

        return null;
    }

    private static ArbiterSquadAgent? SquadAgentOf(string? type) =>
        type is not null
        && !string.Equals(type, "conductor", StringComparison.OrdinalIgnoreCase)
        && ArbiterSquadCatalog.LoadEmbedded().Agents.TryGetValue(type, out ArbiterSquadAgent? agent)
            ? agent
            : null;

    /// <summary>The project's budget, or the shipped one; null when the project turns the Arbiter off.</summary>
    private static ArbiterDispatchBudgetConfig? LoadBudget(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ArbiterDispatchBudgetConfig.Defaults;
        }

        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(workingDirectory);
        if (!loaded.Success || loaded.Config is null)
        {
            return ArbiterDispatchBudgetConfig.Defaults;
        }

        return loaded.Config.Arbiter.ExplicitlyEnabled == false ? null : loaded.Config.Arbiter.DispatchBudget;
    }

    private static string BudgetDirectory(string home) => Path.Combine(home, ".kyber-weave", "arbiter", BudgetDirectoryName);

    private static string KeyOf(string harness, string agentId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(harness + "\n" + agentId)))[..24];

    private static void Sweep(string directory, DateTimeOffset now)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) > BudgetRetention)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file another process holds is swept next time.
            }
        }
    }

    private static string Deny(string harness, string reason) =>
        string.Equals(harness, ClaudeHarness, StringComparison.Ordinal)
            ? new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = reason,
                },
            }.ToJsonString()
            : new JsonObject
            {
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            }.ToJsonString();

    private static JsonObject? ArgumentsOf(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Object => JsonNode.Parse(value.GetRawText()) as JsonObject,
                JsonValueKind.String => JsonNode.Parse(value.GetString() ?? string.Empty) as JsonObject,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string? NodeText(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out JsonNode? value) && value is JsonValue scalar
        && scalar.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static void TryLog(TextWriter log, string message)
    {
        try
        {
            log.WriteLine(message);
        }
        catch (Exception)
        {
            // The log is best effort.
        }
    }

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>One budget window as its file records it: the header, then one timestamp per call.</summary>
    private sealed record Window(DateTimeOffset Start, int Minutes, int MaxCalls, int Calls, DateTimeOffset LastCall)
    {
        public bool IsOver(DateTimeOffset at) => Calls > MaxCalls || at - Start > TimeSpan.FromMinutes(Minutes);

        public static Window? Read(string[] lines)
        {
            string[] header = lines.Length > 0 ? lines[0].Split(' ') : [];
            if (header.Length < 3
                || !long.TryParse(header[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long startMs)
                || !int.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
                || !int.TryParse(header[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxCalls))
            {
                return null;
            }

            long lastMs = startMs;
            if (lines.Length > 1
                && long.TryParse(lines[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                lastMs = parsed;
            }

            return new Window(
                DateTimeOffset.FromUnixTimeMilliseconds(startMs),
                minutes,
                maxCalls,
                lines.Length - 1,
                DateTimeOffset.FromUnixTimeMilliseconds(lastMs));
        }
    }

    private sealed record ToolCall(string Harness, string AgentId, ArbiterSquadAgent Agent, string? Cwd, string? WritePath);
}
