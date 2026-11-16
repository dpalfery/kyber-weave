using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The worker guard's pure half: reading a tool call from a harness event, the budget window, and the refusal documents.</summary>
internal static class WorkerGuardRules
{
    /// <summary>The rule id a stray write is refused under.</summary>
    internal const string CheckoutRuleId = "KW-ARB-CHECKOUT-001";

    /// <summary>The rule id an exhausted budget is refused under.</summary>
    internal const string BudgetRuleId = "KW-ARB-BUDGET-001";

    /// <summary>The status line a worker whose budget ran out returns.</summary>
    internal const string BudgetExceededStatus = "STATUS: BUDGET_EXCEEDED";

    /// <summary>The Copilot CLI harness token.</summary>
    internal const string CopilotCliHarness = "copilot-cli";

    /// <summary>The Claude Code harness token.</summary>
    internal const string ClaudeHarness = "claude";

    /// <summary>The closing line a seat stopped by its budget writes, naming what it did not examine.</summary>
    internal const string SeatBudgetLine = "Budget reached, not examined:";

    /// <summary>The capability profile of a review council seat, which carries the shorter seat budget.</summary>
    internal const string SeatProfile = "review-seat";

    /// <summary>How long a budget file is kept after its last write.</summary>
    internal static readonly TimeSpan BudgetRetention = TimeSpan.FromDays(2);

    /// <summary>The capability profiles whose dispatches carry a budget: the developers and docs-dev.</summary>
    private static readonly HashSet<string> BudgetedProfiles = new(StringComparer.Ordinal) { "worker", "documentation" };

    /// <summary>
    /// Whether the event can need a refusal at all, decided from the event alone: a root agent, an agent outside the Squad
    /// catalog, and a developer-less call that writes no file are never guarded. The guard process asks it before it
    /// builds anything, because the guard runs on every tool call.
    /// </summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <returns>False only when the guard would allow without reading a file; true when it must decide, and when the event is unreadable, so the guard reports it.</returns>
    internal static bool NeedsDecision(string harness, string stdin)
    {
        try
        {
            if (TurnEndOf(harness, stdin) is not null)
            {
                return true;
            }

            ToolCall? call = Read(harness, stdin);
            return call is not null
                && (call.WritePath is not null
                    || string.Equals(call.Agent.CapabilityProfile, SeatProfile, StringComparison.Ordinal)
                    || IsBudgeted(call.Agent.CapabilityProfile));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Whether a dispatch with this capability profile carries a budget.</summary>
    internal static bool IsBudgeted(string capabilityProfile) => BudgetedProfiles.Contains(capabilityProfile);

    /// <summary>
    /// The one-time deny after a call that ran past the per-call limit. The denied call is counted like any other, so
    /// the retry that follows comes right after it and is allowed.
    /// </summary>
    internal static string StalledCallRefusal(TimeSpan gap, int limit) =>
        $"{BudgetRuleId}: your previous tool call ran for about {(int)gap.TotalMinutes} minutes, past the {limit}-minute " +
        "limit for one call. If it started a process that is still running (a hung test run, server or watcher), stop " +
        "that process now. Record NOT VERIFIED: <the command> — timed out in your report, do not run it again unchanged, " +
        "and move on to the rest of your task. This call was denied once; make it again to continue.";

    internal static bool WindowEnded(string[] lines, DateTimeOffset now)
    {
        if (Window.Read(lines) is not { } window)
        {
            // An empty file is one another hook process is still creating; anything else unreadable starts over.
            return lines.Length > 0;
        }

        TimeSpan idle = now - window.LastCall;
        return idle > TimeSpan.FromMinutes(window.Minutes) || (window.IsOver(window.LastCall) && idle > TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The agent whose turn a Copilot CLI <c>subagentStop</c> event ends, or null for any other event. The event
    /// names an agent and a stop reason and no tool.
    /// </summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    internal static string? TurnEndOf(string harness, string stdin)
    {
        if (!string.Equals(harness, CopilotCliHarness, StringComparison.Ordinal))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(stdin);
        JsonElement root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && Text(root, "toolName") is null
            && Text(root, "stopReason") is not null
                ? Text(root, "agentId")
                : null;
    }

    internal static string KeyOf(string harness, string agentId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(harness + "\n" + agentId)))[..24];

    internal static ToolCall? Read(string harness, string stdin)
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

    internal static ArbiterSquadAgent? SquadAgentOf(string? type) =>
        type is not null
        && !string.Equals(type, "conductor", StringComparison.OrdinalIgnoreCase)
        && ArbiterSquadCatalog.LoadEmbedded().Agents.TryGetValue(type, out ArbiterSquadAgent? agent)
            ? agent
            : null;

    /// <summary>The refusal an exhausted budget answers with.</summary>
    internal static string ExhaustedRefusal(bool seat, bool writesSeatFile, int calls, TimeSpan elapsed, int maxCalls, int minutes)
    {
        // A seat past its budget still writes its seat file, so the partial result it was told to write is not lost.
        if (seat)
        {
            return writesSeatFile
                ? string.Empty
                : $"{BudgetRuleId}: this review seat has made {calls} tool calls in {(int)elapsed.TotalMinutes} minutes, past " +
                  $"its budget of {maxCalls} calls or {minutes} minutes. Make no more reads or lookups. Return your output now " +
                  "from what you have: as a lens seat, the findings you can support, then, among the closing lines, " +
                  $"'{SeatBudgetLine} <what your lens did not reach>'; as a refutation, SUPPORTED for every finding you " +
                  "have not defeated with evidence. If your invocation names a Seat file, write it there (that write is " +
                  "allowed); otherwise reply with it.";
        }

        return $"{BudgetRuleId}: this dispatch has made {calls} tool calls in {(int)elapsed.TotalMinutes} minutes, past its " +
               $"budget of {maxCalls} calls or {minutes} minutes. Stop now: make no more tool calls. Return a report whose " +
               $"first line is {BudgetExceededStatus}, then what is done, what is not, and the last failure you saw.";
    }

    /// <summary>The header line a budget file opens with.</summary>
    internal static string HeaderLine(DateTimeOffset now, ArbiterDispatchBudgetConfig budget, string agentName) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{now.ToUnixTimeMilliseconds()} {budget.Minutes} {budget.ToolCalls} {agentName} {budget.CallMinutes}\n");

    /// <summary>The line a call appends to a budget file.</summary>
    internal static string CallLine(DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"{now.ToUnixTimeMilliseconds()}\n");

    internal static string Deny(string harness, string reason) =>
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

    internal static JsonObject? ArgumentsOf(JsonElement root, string name)
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

    internal static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    internal static string? NodeText(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out JsonNode? value) && value is JsonValue scalar
        && scalar.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    internal static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>One budget window as its file records it: the header, then one timestamp per call.</summary>
    internal sealed record Window(DateTimeOffset Start, int Minutes, int MaxCalls, int Calls, DateTimeOffset LastCall, int CallMinutes)
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

            // A header written before the per-call limit existed has four fields; it gets the shipped limit.
            int callMinutes = header.Length >= 5
                && int.TryParse(header[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCall)
                && parsedCall > 0
                    ? parsedCall
                    : ArbiterDispatchBudgetConfig.Defaults.CallMinutes;

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
                DateTimeOffset.FromUnixTimeMilliseconds(lastMs),
                callMinutes);
        }
    }

    internal sealed record ToolCall(string Harness, string AgentId, ArbiterSquadAgent Agent, string? Cwd, string? WritePath);
}
