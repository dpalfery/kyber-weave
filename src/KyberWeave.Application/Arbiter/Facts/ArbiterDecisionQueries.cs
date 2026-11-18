using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>The questions the rules put to the decision log, answered over the decisions a read returned.</summary>
public static class ArbiterDecisionQueries
{
    /// <summary>
    /// The <c>REPEAT</c> count: one plus the number of earlier escalation
    /// decisions with the same plan file, plan digest and task that share
    /// <paramref name="ruleId"/>. The current decision is not in the log yet
    /// when the hook calls this, so "earlier" is every matching decision read.
    /// </summary>
    /// <remarks>
    /// A dispatch with no plan file has no plan to key on, so without a session
    /// every such escalation in the repository would count toward one another.
    /// <c>session</c> is consulted only when the plan file is null, and matching on it
    /// keeps a planless repeat to the same conversation. A null or blank session never matches:
    /// some harnesses send none, the log never expires, and a repeat of two or more stops
    /// the task, so counting those together would stop unrelated conversations for good.
    /// </remarks>
    /// <param name="decisions">The decisions a log read returned, in append order.</param>
    /// <param name="ruleId">The rule that escalated.</param>
    /// <param name="planFile">The plan file the dispatch named, or null.</param>
    /// <param name="planDigest">The plan file's digest, or null.</param>
    /// <param name="task">The task the dispatch named, or null.</param>
    /// <param name="session">The session, consulted only when there is no plan file.</param>
    public static int Repeat(IReadOnlyList<ArbiterDecisionRecord> decisions, string ruleId, string? planFile, string? planDigest, string? task, string? session = null)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return 1 + decisions.Count(decision =>
            string.Equals(decision.Outcome, RuleEffects.Escalate, StringComparison.Ordinal)
            && decision.Rules.Any(rule => string.Equals(rule.Id, ruleId, StringComparison.Ordinal))
            && string.Equals(decision.PlanFile, planFile, StringComparison.Ordinal)
            && string.Equals(decision.PlanDigest, planDigest, StringComparison.Ordinal)
            && string.Equals(decision.Task, task, StringComparison.Ordinal)
            && (planFile is not null
                || (!string.IsNullOrWhiteSpace(session)
                    && string.Equals(decision.Session, session, StringComparison.Ordinal))));
    }
}
