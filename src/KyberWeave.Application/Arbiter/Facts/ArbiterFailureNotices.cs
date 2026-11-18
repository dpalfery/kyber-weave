namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The one-line notice the hook shows on a rework dispatch whose failure is the same as the one before it.
/// The line names the earlier failure and the decision that carries it, so a host can trace the match.
/// </summary>
public static class ArbiterFailureNotices
{
    /// <summary>
    /// The advisory for a rework dispatch whose current failure matches the stored prior failure of its cluster.
    /// </summary>
    /// <param name="priorId">The decision record that carries the earlier failure.</param>
    /// <param name="decisionId">The decision record that judged the current failure.</param>
    /// <returns><c>ARBITER_SAME_FAILURE: yes prior=&lt;id&gt; decision=&lt;id&gt;</c></returns>
    public static string SameFailureLine(string priorId, string decisionId) =>
        $"ARBITER_SAME_FAILURE: yes prior={priorId} decision={decisionId}";
}
