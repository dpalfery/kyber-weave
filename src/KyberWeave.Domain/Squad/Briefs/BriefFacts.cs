namespace KyberWeave.Core.Squad.Briefs;

/// <summary>
/// The facts a brief is selected by. A null fact is unknown, and an unknown fact keeps every
/// section that depends on it rather than guessing.
/// </summary>
/// <param name="Path">
/// The route or dispatch: <c>intake</c>, <c>plan</c> (drafting), <c>finalize</c> (an approved plan's finalization),
/// <c>spec</c> or <c>escalation</c>.
/// </param>
/// <param name="Autonomy">Who answers the gates: <c>supervised</c> or <c>autonomous</c>.</param>
/// <param name="Mode">The plan's development mode: <c>test-first</c> or <c>standard</c>.</param>
/// <param name="Sources">Where each known fact came from, keyed by fact name, for the header.</param>
/// <param name="PlanStatus">
/// The plan's frontmatter status when a plan was read. It only labels the header: it changes during a run,
/// so no section is cut on it.
/// </param>
public sealed record BriefFacts(
    string? Path,
    string? Autonomy,
    string? Mode,
    IReadOnlyDictionary<string, string>? Sources = null,
    string? PlanStatus = null)
{
    /// <summary>The facts a condition can name, in the order the header lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = ["path", "autonomy", "mode"];

    /// <summary>The values each fact accepts.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Vocabulary { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["path"] = ["intake", "plan", "finalize", "spec", "escalation"],
            ["autonomy"] = ["supervised", "autonomous"],
            ["mode"] = ["test-first", "standard"],
        };
}
