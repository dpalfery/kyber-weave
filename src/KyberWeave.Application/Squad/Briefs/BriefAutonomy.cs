using System.Text.RegularExpressions;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Resolves the autonomy fact a brief is selected by.</summary>
public static partial class BriefAutonomy
{
    /// <summary>
    /// Resolves autonomy the way the conductor's autonomy reference states it: a prompt value for
    /// this run, then the value the repository root <c>AGENTS.md</c> Config Reg declares, then the
    /// configured default, then <c>supervised</c>.
    /// </summary>
    /// <param name="prompt">The value given for this run, or null.</param>
    /// <param name="agentsMarkdown">The text of the repository root <c>AGENTS.md</c>, or null when absent.</param>
    /// <param name="configured">The loaded configuration's autonomy setting, or null.</param>
    /// <returns>The resolved value and the name of what supplied it.</returns>
    public static (string Value, string Source) Resolve(string? prompt, string? agentsMarkdown, string? configured)
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

    /// <summary>Reads an autonomy value the way the conductor does: trimmed, case-insensitive.</summary>
    /// <param name="value">The value to read, or null.</param>
    /// <returns><c>supervised</c> or <c>autonomous</c>, or null when the value is neither.</returns>
    public static string? Normalize(string? value)
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
