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
    /// <param name="recommendation">The recommended artifact, <c>PLAN</c> or <c>SPEC</c>; any other value is refused.</param>
    /// <param name="decisionId">The decision record that answered the shortcut.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="recommendation"/> is not <c>PLAN</c> or <c>SPEC</c>.</exception>
    public static string Intake(string input, string recommendation, string decisionId)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(decisionId);
        if (!IsRecommendation(recommendation))
        {
            // Only the two known values are written: any other text could carry a line break that forges a field.
            throw new ArgumentException("The recommendation must be PLAN or SPEC.", nameof(recommendation));
        }

        return string.Join(
            "\n",
            "STATUS: INTAKE_RECOMMENDATION",
            $"INPUT: {OneField(input)}",
            $"RECOMMENDATION: {recommendation}",
            $"RATIONALE: Arbiter shortcut (decision {decisionId})",
            "GAPS: none",
            "OPEN_QUESTIONS: none",
            $"ARBITER_DECISION: {decisionId}");
    }

    /// <summary>Whether <paramref name="recommendation"/> is one of the two artifacts an intake digest may name, exactly as written.</summary>
    public static bool IsRecommendation(string recommendation) =>
        string.Equals(recommendation, "PLAN", StringComparison.Ordinal)
        || string.Equals(recommendation, "SPEC", StringComparison.Ordinal);

    /// <summary>The most characters one digest field carries from operator or caller text.</summary>
    private const int MaxFieldCharacters = 500;

    /// <summary>
    /// Flattens caller text to one line for a single digest field: CR and LF become spaces, so the text cannot forge a
    /// field of its own, and the text is capped so a long intake cannot swamp the digest.
    /// </summary>
    private static string OneField(string text)
    {
        string flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= MaxFieldCharacters ? flat : flat[..MaxFieldCharacters];
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
