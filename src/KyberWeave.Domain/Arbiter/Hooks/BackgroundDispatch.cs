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

/// <summary>
/// The part of the hook decision engine that follows a background dispatch: it remembers which pre event the
/// agent answers, and records each finished turn as that dispatch's return.
/// </summary>
public interface IBackgroundDispatchEngine
{
    /// <summary>
    /// Remembers a background dispatch against the pre event it started from. Records no return. A dispatch with
    /// no pre event on the ledger is not remembered.
    /// </summary>
    /// <param name="dispatch">The dispatch that started.</param>
    /// <param name="context">What the event is evaluated against.</param>
    void TrackBackgroundDispatch(BackgroundDispatch dispatch, HookContext context);

    /// <summary>
    /// Records one finished turn of a remembered background agent as the return of its dispatch and runs the return
    /// rules on it. An agent that was never remembered, or a turn already recorded, passes through.
    /// </summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="sessionId">The harness session the agent runs in.</param>
    /// <param name="agentId">The harness's id for the agent.</param>
    /// <param name="response">The agent's final text for the turn.</param>
    /// <param name="config">The loaded host configuration.</param>
    /// <param name="context">What the event is evaluated against.</param>
    HookOutcome DecideBackgroundReturn(
        string harness,
        string sessionId,
        string agentId,
        string response,
        KyberWeaveConfig config,
        HookContext context);

    /// <summary>
    /// Hands over, once, the Arbiter notes on background returns that <paramref name="caller"/> dispatched in this
    /// session and has not been shown through a dispatch hook yet.
    /// </summary>
    /// <param name="sessionId">The harness session.</param>
    /// <param name="caller">The dispatching agent, or <c>unidentified</c>.</param>
    /// <param name="context">What the event is evaluated against.</param>
    IReadOnlyList<string> TakePendingNotes(string sessionId, string caller, HookContext context);
}
