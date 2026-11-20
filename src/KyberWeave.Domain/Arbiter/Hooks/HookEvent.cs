using System.Text.Json;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// One harness event in the Arbiter's own terms: the adapter reads the harness payload and everything after this
/// boundary — guards, rules and the hook host — reads this record.
/// </summary>
/// <param name="Harness">The harness token the event arrived from.</param>
/// <param name="Cwd">The working directory the payload names, or null when it names none.</param>
/// <param name="WorkspaceRoot">The workspace root the payload names, for a harness that reports one.</param>
/// <param name="AgentId">The sub-agent's id, when the event comes from inside one.</param>
/// <param name="AgentType">The sub-agent's type, when the harness reports it.</param>
/// <param name="ToolName">The tool the event is for, or null when it is not a tool call.</param>
/// <param name="CallId">The harness's id for this call, when it reports one.</param>
/// <param name="SessionId">The harness session id, when it reports one.</param>
/// <param name="Prompt">The dispatch prompt the event carries, or null when it carries none.</param>
/// <param name="Arguments">The tool arguments, or <c>Undefined</c> when the event carries none.</param>
public sealed record HookEvent(
    string Harness,
    string? Cwd = null,
    string? WorkspaceRoot = null,
    string? AgentId = null,
    string? AgentType = null,
    string? ToolName = null,
    string? CallId = null,
    string? SessionId = null,
    string? Prompt = null,
    JsonElement Arguments = default);
