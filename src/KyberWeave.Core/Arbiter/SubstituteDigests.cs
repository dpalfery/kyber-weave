namespace KyberWeave.Core.Arbiter;

/// <summary>
/// The substitute digests an answered shortcut hands back in place of the worker's own report. A worker or
/// reviewer parses these fields by name and order, so the text is the contract.
/// </summary>
/// <remarks>
/// Each digest is one line per field, joined with <c>\n</c>, with no trailing newline. The docs digest's
/// justification separator is an em dash, as written in the approved design.
/// </remarks>
public static class SubstituteDigests
{
    /// <summary>The substitute report for a bug-crusher investigate the Arbiter judged trivial.</summary>
    /// <param name="decisionId">The decision record that answered the shortcut.</param>
    public static string Triage(string decisionId)
    {
        ArgumentNullException.ThrowIfNull(decisionId);
        return string.Join(
            "\n",
            "REPRODUCED: not-run (Arbiter shortcut)",
            $"ROOT_CAUSE: trivial single-site defect (Arbiter decision {decisionId})",
            "BLAST_RADIUS: single file",
            "VERDICT: TRIVIAL",
            $"ARBITER_DECISION: {decisionId}");
    }

    /// <summary>The substitute recommendation for a conductor's intake the Arbiter answered without a planner.</summary>
    /// <param name="input">The intake request the planner was asked to assess.</param>
    /// <param name="recommendation">The recommended artifact, <c>PLAN</c> or <c>SPEC</c>.</param>
    /// <param name="decisionId">The decision record that answered the shortcut.</param>
    public static string Intake(string input, string recommendation, string decisionId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(decisionId);
        return string.Join(
            "\n",
            "STATUS: INTAKE_RECOMMENDATION",
            $"INPUT: {input}",
            $"RECOMMENDATION: {recommendation}",
            $"RATIONALE: Arbiter shortcut (decision {decisionId})",
            "GAPS: none",
            "OPEN_QUESTIONS: none",
            $"ARBITER_DECISION: {decisionId}");
    }

    /// <summary>The substitute report for a docs-dev dispatch whose change needs no documentation.</summary>
    /// <param name="decisionId">The decision record that answered the shortcut.</param>
    public static string DocsImpact(string decisionId)
    {
        ArgumentNullException.ThrowIfNull(decisionId);
        return string.Join(
            "\n",
            $"DOCS_REVIEW: no doc change with justification — Arbiter shortcut (decision {decisionId})",
            "RECOMMENDATIONS: none",
            $"ARBITER_DECISION: {decisionId}");
    }
}
