using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The worker guard's pure half: reading a tool call from a harness event, the budget window, and the refusal documents.</summary>
internal static class WorkerGuardRules
{
    /// <summary>The rule id a stray write is refused under.</summary>
    internal const string CheckoutRuleId = "KW-ARB-CHECKOUT-001";

    /// <summary>The rule id an exhausted budget is refused under.</summary>
    internal const string BudgetRuleId = "KW-ARB-BUDGET-001";

    /// <summary>The trigger a sub-agent's own command call raises, so a technology's rules can allow-list its forms.</summary>
    internal const string CommandTrigger = "worker.command";

    /// <summary>The fact holding the command as the harness reported it.</summary>
    internal const string CommandTextFact = "command.text";

    /// <summary>The fact holding the command's form: its program and leading words, options and their values removed.</summary>
    internal const string CommandFormFact = "command.form";

    /// <summary>The fact that is true when the command is one command, with no join, redirect, subshell or substitution.</summary>
    internal const string CommandSingleFact = "command.single";

    /// <summary>The fact holding the form's words before its first option, where a mutating verb appears.</summary>
    internal const string CommandHeadFact = "command.head";

    /// <summary>The fact holding every word of the command, so a rule sees the tool however it was reached.</summary>
    internal const string CommandWordsFact = "command.words";

    /// <summary>The fact holding the command's program, the form's first word.</summary>
    internal const string CommandProgramFact = "command.program";

    /// <summary>The fact naming the calling sub-agent.</summary>
    internal const string WorkerNameFact = "worker.name";

    /// <summary>The fact naming the calling sub-agent's capability profile.</summary>
    internal const string WorkerProfileFact = "worker.capability-profile";

    /// <summary>The fact naming the harness the call arrived from.</summary>
    internal const string WorkerHarnessFact = "worker.harness";

    /// <summary>The capability profile whose command calls a technology's allow-list judges: read-only, can run commands.</summary>
    internal const string PlatformReaderProfile = "platform-reader";

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

    /// <summary>The leading words a command may carry before its program, which the form skips.</summary>
    private static readonly HashSet<string> CommandWrappers = new(StringComparer.Ordinal)
    {
        "sudo", "env", "command", "exec", "nohup",
    };

    /// <summary>The characters that join, redirect, group or negate commands: a command holding one has no single form.</summary>
    private static readonly System.Buffers.SearchValues<char> ShellControl =
        System.Buffers.SearchValues.Create(";&|<>`(){}!~\n\r");

    /// <summary>The punctuation a word may still carry from the shell around it, removed before a rule reads it.</summary>
    private static readonly char[] WordWrapping = ['\'', '"', '(', ')', '{', '}', '!', '~', '`', ';', '&', '|', '<', '>'];

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
                    || IsCommandJudged(call)
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

    /// <summary>Whether a technology's command allow-list judges this call's command.</summary>
    /// <remarks>
    /// Only a profile that may run commands and may not write files is judged: a developer's commands are its
    /// work, and judging them would refuse every build and test run.
    /// </remarks>
    internal static bool IsCommandJudged(ToolCall call) =>
        call.Command is { Length: > 0 }
        && string.Equals(call.Agent.CapabilityProfile, PlatformReaderProfile, StringComparison.Ordinal);

    /// <summary>
    /// The refusal a technology's command allow-list makes, or null when no rule refuses this command. The rules of
    /// every loaded technology folder are evaluated together; each rule narrows itself to its own tool's commands.
    /// </summary>
    internal static string? CommandRefusal(IReadOnlyList<ArbiterRule> rules, ToolCall call, string command)
    {
        ArgumentNullException.ThrowIfNull(rules);

        ArbiterOutcome outcome = RuleEngine.EvaluateStep0(rules, CommandFacts(call, command), CommandTrigger);
        if (!string.Equals(outcome.CombinedEffect, RuleEffects.Escalate, StringComparison.Ordinal))
        {
            return null;
        }

        string refusing = string.Join(", ", outcome.RuleAnswers
            .Where(answer => !answer.Shadow
                && string.Equals(answer.Effect, RuleEffects.Escalate, StringComparison.Ordinal))
            .Select(answer => $"{answer.RuleId} ({answer.Answer})")
            .Distinct(StringComparer.Ordinal));
        return $"{refusing}: this command is outside the forms this technology allows a read-only agent to run " +
            $"('{ShapeOf(command).Form}'). Nothing ran. Use a listed read form, or report what you could not inspect.";
    }

    /// <summary>The facts a <c>worker.command</c> rule reads, from the call the harness reported.</summary>
    internal static ArbiterFactSet CommandFacts(ToolCall call, string command)
    {
        CommandShape shape = ShapeOf(command);
        return new ArbiterFactSet()
            .With(WorkerNameFact, call.Agent.Name, ArbiterFactLabel.Derived)
            .With(WorkerProfileFact, call.Agent.CapabilityProfile, ArbiterFactLabel.Derived)
            .With(WorkerHarnessFact, call.Harness, ArbiterFactLabel.Derived)
            .With(CommandTextFact, shape.Text, ArbiterFactLabel.Asserted)
            .With(CommandFormFact, shape.Form, ArbiterFactLabel.Asserted)
            .With(CommandSingleFact, shape.Single, ArbiterFactLabel.Derived)
            .With(CommandHeadFact, shape.Head, ArbiterFactLabel.Asserted)
            .With(CommandProgramFact, shape.Program, ArbiterFactLabel.Asserted)
            .With(CommandWordsFact, shape.Words, ArbiterFactLabel.Asserted);
    }

    /// <summary>A command's form, whether it is one command, its head words, its program, and every word it names.</summary>
    /// <param name="Text">The command as reported, whitespace collapsed.</param>
    /// <param name="Form">The single form's program and leading words, or the whole text when there is no single form.</param>
    /// <param name="Single">Whether the command is one command, with no join, redirect, subshell or substitution.</param>
    /// <param name="Head">The form's words before its first option, where a mutating verb appears; empty when not one command.</param>
    /// <param name="Program">The form's first word, or null when the command has no single form.</param>
    /// <param name="Words">Every word the command names, with quotes and wrapping punctuation removed and a path's directory stripped.</param>
    private sealed record CommandShape(
        string Text,
        string Form,
        bool Single,
        IReadOnlyList<string> Head,
        string? Program,
        IReadOnlyList<string> Words);

    /// <summary>
    /// Reads one command into the shape a rule judges. A single command yields its form and its head; a compound
    /// command, a redirect, a subshell, a grouping or a substitution yields its whole text, no head and no program,
    /// because it is not one form and no listed form may allow it. Its words still name every tool it reaches, which
    /// is how a technology's rule refuses it.
    /// </summary>
    private static CommandShape ShapeOf(string command)
    {
        string text = Collapse(command);
        IReadOnlyList<string> words = WordsOf(text);
        if (!IsSingleCommand(command))
        {
            return new CommandShape(text, text, Single: false, Head: [], Program: null, words);
        }

        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int start = 0;
        while (start < tokens.Length
               && (CommandWrappers.Contains(tokens[start]) || IsAssignment(tokens[start])))
        {
            start++;
        }

        List<string> leading = [];
        for (int index = start; index < tokens.Length; index++)
        {
            if (tokens[index].StartsWith('-'))
            {
                break;
            }

            leading.Add(tokens[index]);
        }

        string form = leading.Count == 0 ? text : string.Join(' ', leading);
        return new CommandShape(
            text,
            form,
            Single: true,
            Head: leading,
            Program: leading.Count > 0 ? Normalise(leading[0]) : null,
            words);
    }

    /// <summary>
    /// Whether the command is exactly one command: no unquoted character joins, redirects, groups or substitutes,
    /// and no <c>$(</c> outside single quotes. Quotes are honoured so a query such as <c>--query "{a:b}"</c> is still
    /// one command.
    /// </summary>
    private static bool IsSingleCommand(string command)
    {
        bool singleQuoted = false;
        bool doubleQuoted = false;
        for (int index = 0; index < command.Length; index++)
        {
            char current = command[index];
            if (singleQuoted)
            {
                if (current == '\'')
                {
                    singleQuoted = false;
                }

                continue;
            }

            if (current == '\'')
            {
                singleQuoted = true;
                continue;
            }

            if (doubleQuoted)
            {
                // A substitution runs inside double quotes, so only the closing quote ends the literal run.
                if (current == '`'
                    || (current == '$' && index + 1 < command.Length && command[index + 1] == '('))
                {
                    return false;
                }

                if (current == '"')
                {
                    doubleQuoted = false;
                }

                continue;
            }

            if (current == '"')
            {
                doubleQuoted = true;
                continue;
            }

            if (ShellControl.Contains(current)
                || (current == '$' && index + 1 < command.Length && command[index + 1] == '('))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every word the command names, for a rule that must see the tool however it was reached: a quoted program, an
    /// absolute path, a word inside a compound command, or a token still carrying a subshell's bracket.
    /// </summary>
    private static IReadOnlyList<string> WordsOf(string text)
    {
        List<string> words = [];
        foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string word = Normalise(token);
            if (word.Length > 0)
            {
                words.Add(word);
            }
        }

        return words;
    }

    /// <summary>
    /// One word reduced to the name a rule can match: quotes and the shell's wrapping punctuation removed, and a
    /// path's directory stripped so <c>/usr/bin/az</c> is <c>az</c>.
    /// </summary>
    private static string Normalise(string token)
    {
        string trimmed = token.Trim(WordWrapping);
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static bool IsAssignment(string token)
    {
        int equals = token.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 && token[..equals].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static string Collapse(string command) =>
        string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
            string? command = tool is "bash" && args is not null ? NodeText(args, "command") : null;
            return new ToolCall(harness, agentId, agent, cwd, path, command);
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
            string? command = tool is "Bash" && input is not null ? NodeText(input, "command") : null;
            return new ToolCall(harness, agentId, agent, cwd, path, command);
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

    internal sealed record ToolCall(
        string Harness,
        string AgentId,
        ArbiterSquadAgent Agent,
        string? Cwd,
        string? WritePath,
        string? Command);
}
