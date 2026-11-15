using System.Text.RegularExpressions;

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
public sealed partial record BriefFacts(
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

    /// <summary>The value of the named fact, or null when it is unknown.</summary>
    public string? Get(string fact) => fact switch
    {
        "path" => Path,
        "autonomy" => Autonomy,
        "mode" => Mode,
        _ => null,
    };

    /// <summary>
    /// Resolves autonomy the way the conductor's autonomy reference states it: a prompt value for
    /// this run, then the value the repository root <c>AGENTS.md</c> Config Reg declares, then the
    /// configured default, then <c>supervised</c>.
    /// </summary>
    /// <param name="prompt">The value given for this run, or null.</param>
    /// <param name="agentsMarkdown">The text of the repository root <c>AGENTS.md</c>, or null when absent.</param>
    /// <param name="configured">The loaded configuration's autonomy setting, or null.</param>
    /// <returns>The resolved value and the name of what supplied it.</returns>
    public static (string Value, string Source) ResolveAutonomy(string? prompt, string? agentsMarkdown, string? configured)
    {
        string? fromPrompt = Normalize(prompt);
        if (fromPrompt is not null)
        {
            return (fromPrompt, "prompt");
        }

        string? fromRegistry = agentsMarkdown is null ? null : Normalize(RegistryAutonomy(agentsMarkdown));
        if (fromRegistry is not null)
        {
            return (fromRegistry, "AGENTS.md Config Reg");
        }

        string? fromConfig = Normalize(configured);
        return fromConfig is not null ? (fromConfig, "configuration") : ("supervised", "default");
    }

    private static string? Normalize(string? value)
    {
        string? trimmed = value?.Trim();
        if (string.Equals(trimmed, "supervised", StringComparison.OrdinalIgnoreCase))
        {
            return "supervised";
        }

        return string.Equals(trimmed, "autonomous", StringComparison.OrdinalIgnoreCase) ? "autonomous" : null;
    }

    private static string? RegistryAutonomy(string markdown)
    {
        Match match = RegistryLine().Match(markdown);
        return match.Success ? match.Groups["value"].Value : null;
    }

    [GeneratedRegex(@"^\s*[-*]\s*\*\*<autonomy>\*\*\s*:\s*`?(?<value>[A-Za-z-]+)`?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RegistryLine();
}
