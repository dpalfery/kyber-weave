using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// A sub-agent dispatch the harness started in the background: its dispatch tool returned at once with only the
/// agent's id, and each of the agent's turns ends later with its own stop event.
/// </summary>
/// <param name="Harness">The harness token.</param>
/// <param name="SessionId">The harness session the agent runs in.</param>
/// <param name="AgentId">The harness's id for the agent.</param>
/// <param name="Caller">The dispatching agent, or <c>unidentified</c>.</param>
/// <param name="Target">The dispatched agent type.</param>
/// <param name="Prompt">The dispatch prompt as executed.</param>
public sealed record BackgroundDispatch(
    string Harness,
    string SessionId,
    string AgentId,
    string Caller,
    string? Target,
    string Prompt);

/// <summary>What the Arbiter keeps about a background dispatch between its start and each of its turns ending.</summary>
/// <param name="Dispatch">The dispatch as it started.</param>
/// <param name="PreId">The ledger id of the dispatch's pre event, which every turn's return is paired with.</param>
/// <param name="LastResponseDigest">SHA-256 of the last turn's response that was recorded, so a repeat is not recorded twice.</param>
/// <param name="PendingNote">An Arbiter note on the last return that the dispatching agent has not been shown yet.</param>
public sealed record BackgroundDispatchRecord(
    BackgroundDispatch Dispatch,
    string PreId,
    string? LastResponseDigest = null,
    string? PendingNote = null);
