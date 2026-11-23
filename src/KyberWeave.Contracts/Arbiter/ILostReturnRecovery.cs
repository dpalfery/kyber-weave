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
    /// is logged, because closing the wrong one reports work as finished that never ran.
    /// </remarks>
    /// <param name="harness">The harness token that delivered the stop event.</param>
    /// <param name="sessionId">The harness session the agent ran in, when the payload names one.</param>
    /// <param name="agentNames">Every name the stop event gives the agent; empty when it gives none.</param>
    /// <param name="context">What the event is evaluated against.</param>
    /// <returns>How many dispatches were closed: 0 or 1.</returns>
    int RecoverLostReturn(string harness, string? sessionId, IReadOnlyList<string> agentNames, HookContext context);
}
