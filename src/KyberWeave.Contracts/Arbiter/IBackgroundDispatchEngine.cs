using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

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
    /// Reads the saved record of a followed background agent, so a later hook process can tell whether the agent
    /// was followed and which dispatch it answers.
    /// </summary>
    /// <param name="sessionId">The harness session the agent runs in.</param>
    /// <param name="agentId">The harness's id for the agent.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <returns>The record, or null when the agent is not followed.</returns>
    BackgroundDispatchRecord? FindBackgroundDispatch(string sessionId, string agentId, HookContext context);

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
    /// Records one finished turn of a remembered background agent as the return of its dispatch and runs the return
    /// rules on it, without blocking a thread.
    /// </summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="sessionId">The harness session the agent runs in.</param>
    /// <param name="agentId">The harness's id for the agent.</param>
    /// <param name="response">The agent's final text for the turn.</param>
    /// <param name="config">The loaded host configuration.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    /// <returns>What the rules decided for the return.</returns>
    Task<HookOutcome> DecideBackgroundReturnAsync(
        string harness,
        string sessionId,
        string agentId,
        string response,
        KyberWeaveConfig config,
        HookContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands over, once, the Arbiter notes on background returns that <paramref name="caller"/> dispatched in this
    /// session and has not been shown through a dispatch hook yet.
    /// </summary>
    /// <param name="sessionId">The harness session.</param>
    /// <param name="caller">The dispatching agent, or <c>unidentified</c>.</param>
    /// <param name="context">What the event is evaluated against.</param>
    IReadOnlyList<string> TakePendingNotes(string sessionId, string caller, HookContext context);

    /// <summary>
    /// Keeps the report of an agent that returned before the Arbiter knew its dispatch, so the dispatch's completed
    /// event, a separate hook process, can record it with the dispatch's own prompt and target.
    /// </summary>
    /// <param name="sessionId">The harness session the agent runs in.</param>
    /// <param name="agentId">The harness's id for the agent.</param>
    /// <param name="message">The agent's report.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <remarks>
    /// The default keeps nothing, so an engine that does not follow returns across hook processes still compiles and
    /// behaves as before: the completed event then finds no report and records nothing.
    /// </remarks>
    void KeepUnpairedReturn(string sessionId, string agentId, string message, HookContext context)
    {
    }

    /// <summary>Takes, once, the report kept for an agent by <see cref="KeepUnpairedReturn"/>.</summary>
    /// <param name="sessionId">The harness session the agent runs in.</param>
    /// <param name="agentId">The harness's id for the agent.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <returns>The kept report, or null when none is kept. The default keeps nothing, so it returns null.</returns>
    string? TakeUnpairedReturn(string sessionId, string agentId, HookContext context) => null;
}
