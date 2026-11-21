using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Joins Factory's session events into the ledger that tells the guard which sub-agent made a tool call. Factory's
/// tool-call payload names no agent, so the guard learns it from the root's <c>Task</c> dispatch, the child's
/// <c>SessionStart</c>, and the dispatch's <c>PostToolUse</c> text, and forgets it when the child session ends.
/// </summary>
/// <remarks>
/// The in-flight list is append-only, like the budget files: a <c>+</c> line records a dispatched type and a <c>-</c>
/// line takes one back, so parallel hook processes never lose an entry. A child's type is authoritative once the
/// dispatch's <c>PostToolUse</c> names it, which corrects a guess taken when the child started.
/// </remarks>
internal static class WorkerSessionLedger
{
    private const string AddPrefix = "+";
    private const string TakePrefix = "-";
    private const string PendingKeyPrefix = "pending-";
    private const string SessionIdLine = "session_id:";
    private const string AgentTypeLine = "subagent_type:";

    /// <summary>Whether this event is one the ledger consumes, decided from the event alone.</summary>
    internal static bool Handles(string harness, string stdin)
    {
        if (!string.Equals(harness, WorkerGuardRules.FactoryHarness, StringComparison.Ordinal))
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(stdin);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? eventName = Text(root, "hook_event_name");
        if (eventName is "SessionStart" or "SessionEnd" or "Stop")
        {
            return true;
        }

        return eventName is "PreToolUse" or "PostToolUse"
            && string.Equals(Text(root, "tool_name"), "Task", StringComparison.Ordinal);
    }

    /// <summary>Applies one Factory session event to the ledger.</summary>
    internal static void Apply(string harness, string stdin, string home, IWorkerSessionStore sessions)
    {
        if (!string.Equals(harness, WorkerGuardRules.FactoryHarness, StringComparison.Ordinal))
        {
            return;
        }

        using JsonDocument document = JsonDocument.Parse(stdin);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? eventName = Text(root, "hook_event_name");
        switch (eventName)
        {
            case "SessionStart":
                StartChild(root, home, sessions);
                break;
            case "SessionEnd":
            case "Stop":
                EndChild(root, home, sessions);
                break;
            case "PreToolUse" when string.Equals(Text(root, "tool_name"), "Task", StringComparison.Ordinal):
                RecordDispatch(root, home, sessions);
                break;
            case "PostToolUse" when string.Equals(Text(root, "tool_name"), "Task", StringComparison.Ordinal):
                RecordReturn(root, home, sessions);
                break;
        }
    }

    /// <summary>The key one child session is recorded under.</summary>
    internal static string ChildKey(string sessionId) => WorkerGuardRules.KeyOf(WorkerGuardRules.FactoryHarness, sessionId);

    /// <summary>The key one parent's in-flight list is recorded under.</summary>
    internal static string PendingKey(string parentSessionId) =>
        PendingKeyPrefix + WorkerGuardRules.KeyOf(WorkerGuardRules.FactoryHarness, parentSessionId);

    /// <summary>The types still in flight after the append-only list's <c>+</c> and <c>-</c> lines are counted.</summary>
    internal static IReadOnlyList<string> PendingTypes(IEnumerable<string> lines)
    {
        List<string> order = [];
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.StartsWith(AddPrefix, StringComparison.Ordinal) && line.Length > AddPrefix.Length)
            {
                string type = line[AddPrefix.Length..];
                if (!counts.ContainsKey(type))
                {
                    order.Add(type);
                }

                counts[type] = counts.GetValueOrDefault(type) + 1;
            }
            else if (line.StartsWith(TakePrefix, StringComparison.Ordinal) && line.Length > TakePrefix.Length)
            {
                string type = line[TakePrefix.Length..];
                if (counts.TryGetValue(type, out int count) && count > 0)
                {
                    counts[type] = count - 1;
                }
            }
        }

        return [.. order.Where(type => counts[type] > 0)];
    }

    private static void StartChild(JsonElement root, string home, IWorkerSessionStore sessions)
    {
        string? childId = Text(root, "session_id");
        string? parentId = Text(root, "calling_session_id");
        if (childId is null || parentId is null)
        {
            // A root session's own start names no caller and records nothing.
            return;
        }

        sessions.EnsureDirectory(home);
        string key = ChildKey(childId);
        WorkerSessionEntry? existing = sessions.Read(home, key);
        if (existing is null)
        {
            existing = new WorkerSessionEntry(parentId, AgentType: null);
            sessions.Write(home, key, existing);
        }
        else if (!string.Equals(existing.ParentSessionId, parentId, StringComparison.Ordinal))
        {
            existing = existing with { ParentSessionId = parentId };
            sessions.Write(home, key, existing);
        }

        if (existing.AgentType is not null)
        {
            // The dispatch's return already named the type; it is authoritative.
            return;
        }

        string pendingKey = PendingKey(parentId);
        IReadOnlyList<string> pending = PendingTypes(sessions.ReadPending(home, pendingKey));
        if (pending.Count != 1)
        {
            // Several dispatches are in flight, so this start cannot say which type it is. The
            // dispatch's PostToolUse text resolves it by naming the child session.
            return;
        }

        sessions.Write(home, key, existing with { AgentType = pending[0] });
        sessions.AppendPending(home, pendingKey, TakePrefix + pending[0]);
    }

    private static void EndChild(JsonElement root, string home, IWorkerSessionStore sessions)
    {
        if (Text(root, "session_id") is { } childId)
        {
            sessions.Delete(home, ChildKey(childId));
        }
    }

    private static void RecordDispatch(JsonElement root, string home, IWorkerSessionStore sessions)
    {
        string? parentId = Text(root, "session_id");
        string? type = SubagentTypeOf(root);
        if (parentId is null || type is null)
        {
            return;
        }

        sessions.EnsureDirectory(home);
        sessions.AppendPending(home, PendingKey(parentId), AddPrefix + type);
    }

    private static void RecordReturn(JsonElement root, string home, IWorkerSessionStore sessions)
    {
        string? parentId = Text(root, "session_id");
        string? response = ResponseText(root);
        if (parentId is null || response is null)
        {
            return;
        }

        string? childId = LineValue(response, SessionIdLine);
        string? type = LineValue(response, AgentTypeLine) ?? SubagentTypeOf(root);
        if (childId is null || type is null)
        {
            return;
        }

        sessions.EnsureDirectory(home);
        sessions.Write(home, ChildKey(childId), new WorkerSessionEntry(parentId, type));

        // The type may already have been taken when the child started, or may still be in flight
        // for another child of the same type; taking it again leaves the count correct either way.
        string pendingKey = PendingKey(parentId);
        if (PendingTypes(sessions.ReadPending(home, pendingKey)).Contains(type, StringComparer.Ordinal))
        {
            sessions.AppendPending(home, pendingKey, TakePrefix + type);
        }
    }

    private static string? SubagentTypeOf(JsonElement root) =>
        root.TryGetProperty("tool_input", out JsonElement input) && input.ValueKind == JsonValueKind.Object
            ? Text(input, "subagent_type")
            : null;

    private static string? ResponseText(JsonElement root)
    {
        if (!root.TryGetProperty("tool_response", out JsonElement response))
        {
            return null;
        }

        return response.ValueKind switch
        {
            JsonValueKind.String => response.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => response.GetRawText(),
            _ => null,
        };
    }

    private static string? LineValue(string text, string prefix)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                string value = trimmed[prefix.Length..].Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
