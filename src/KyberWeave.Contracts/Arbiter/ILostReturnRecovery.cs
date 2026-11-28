using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The part of the hook decision engine that closes a dispatch whose return was lost: the harness killed the return
/// hook, or never delivered it, and the dispatch would look in flight for the rest of the run.
/// </summary>
/// <remarks>
/// A harness reports the end of a sub-agent through a stop event (<c>subagentStop</c> on Copilot CLI,
/// <c>SubagentStop</c> on Claude and Factory). That event is evidence the agent's work is over, so a single still-open
/// pre event for that agent is closed with a post row marked recovered.
/// </remarks>
public interface ILostReturnRecovery
{
    /// <summary>
    /// Closes the one dispatch <paramref name="agentNames"/> left open in <paramref name="sessionId"/>.
    /// </summary>
    /// <remarks>
    /// A dispatch the rules refused wrote a pre event but never ran a worker, so it is not a candidate. When several
    /// dispatches of the agent are still open the stop event cannot say which one ended: none is closed and one line
    /// is logged, because closing the wrong one reports work as finished that never ran. A stop event that names the
    /// agent's id closes exactly the dispatch the engine followed for that id, when its pre event is still open, and
    /// nothing else; an id with no followed dispatch falls back to the name rule.
    /// </remarks>
    /// <param name="harness">The harness token that delivered the stop event.</param>
    /// <param name="sessionId">The harness session the agent ran in, when the payload names one.</param>
    /// <param name="agentNames">Every name the stop event gives the agent; empty when it gives none.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <param name="agentId">The harness's id for the agent, when the stop event carries one.</param>
    /// <param name="stopEvent">The event that ended the agent, as the closing row's <c>recovered</c> value; null names
    /// the harness's own stop event.</param>
    /// <returns>How many dispatches were closed: 0 or 1.</returns>
    int RecoverLostReturn(
        string harness,
        string? sessionId,
        IReadOnlyList<string> agentNames,
        HookContext context,
        string? agentId = null,
        string? stopEvent = null);

    /// <summary>
    /// Closes the one dispatch <paramref name="agentNames"/> left open in <paramref name="sessionId"/>, without
    /// blocking a thread.
    /// </summary>
    /// <param name="harness">The harness token that delivered the stop event.</param>
    /// <param name="sessionId">The harness session the agent ran in, when the payload names one.</param>
    /// <param name="agentNames">Every name the stop event gives the agent; empty when it gives none.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <param name="agentId">The harness's id for the agent, when the stop event carries one.</param>
    /// <param name="stopEvent">The event that ended the agent, as the closing row's <c>recovered</c> value; null names
    /// the harness's own stop event.</param>
    /// <param name="cancellationToken">Cancels the recovery.</param>
    /// <returns>How many dispatches were closed: 0 or 1.</returns>
    Task<int> RecoverLostReturnAsync(
        string harness,
        string? sessionId,
        IReadOnlyList<string> agentNames,
        HookContext context,
        string? agentId = null,
        string? stopEvent = null,
        CancellationToken cancellationToken = default);
}
