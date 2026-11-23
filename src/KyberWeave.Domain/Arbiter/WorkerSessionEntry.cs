namespace KyberWeave.Core.Arbiter;

/// <summary>
/// One Factory child session as the worker guard's ledger records it: the parent session that dispatched it, and the
/// Squad agent type it runs as once that is known.
/// </summary>
/// <param name="ParentSessionId">The Factory session id of the parent that dispatched the child.</param>
/// <param name="AgentType">The Squad agent type, or null while the events have not resolved it.</param>
public sealed record WorkerSessionEntry(string ParentSessionId, string? AgentType);
