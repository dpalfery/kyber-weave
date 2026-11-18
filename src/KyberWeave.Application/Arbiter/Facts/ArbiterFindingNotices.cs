namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The one-line notices the hook shows for a finding the Arbiter dropped or found a duplicate. Each is
/// a single line naming the finding, so a host can read which finding was changed and by which decision.
/// </summary>
public static class ArbiterFindingNotices
{
    /// <summary>The rule id the nitpick filter reports under.</summary>
    private const string NitpickRuleId = "KW-ARB-NITPICK-001";

    /// <summary>The line for a finding the nitpick filter dropped.</summary>
    /// <param name="findingId">The dropped finding's id.</param>
    /// <param name="decisionId">The decision record that dropped it.</param>
    /// <returns><c>ARBITER_DROPPED: &lt;id&gt; rule=KW-ARB-NITPICK-001 decision=&lt;id&gt;</c></returns>
    public static string DroppedLine(string findingId, string decisionId) =>
        $"ARBITER_DROPPED: {findingId} rule={NitpickRuleId} decision={decisionId}";

    /// <summary>The line for a refutation whose finding repeats an earlier one.</summary>
    /// <param name="findingId">The refuted finding's id.</param>
    /// <param name="earlierId">The earlier finding it repeats.</param>
    /// <param name="decisionId">The decision record that found the duplicate.</param>
    /// <returns><c>ARBITER_DUPLICATE: &lt;id&gt; of &lt;earlier id&gt; decision=&lt;id&gt;</c></returns>
    public static string DuplicateLine(string findingId, string earlierId, string decisionId) =>
        $"ARBITER_DUPLICATE: {findingId} of {earlierId} decision={decisionId}";
}
