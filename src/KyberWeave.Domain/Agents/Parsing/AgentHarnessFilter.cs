using KyberWeave.Core.Agents.Model;

namespace KyberWeave.Core.Agents.Parsing;

/// <summary>Maps the harnesses the CLI names and the harness folders it discovers.</summary>
public static class AgentHarnessFilter
{
    private static readonly Dictionary<string, HarnessKind> FolderToHarness =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".codex"] = HarnessKind.Codex,
            [".cursor"] = HarnessKind.Cursor,
            [".claude"] = HarnessKind.Claude,
            [".github"] = HarnessKind.GitHubCopilot,
            [".opencode"] = HarnessKind.OpenCode,
            [".kilo"] = HarnessKind.Kilo,
        };

    /// <summary>Parses a CLI harness filter such as <c>cursor</c>, <c>github</c>, or <c>GitHubCopilot</c>.</summary>
    /// <param name="value">The filter value; empty or null means every discovered harness.</param>
    /// <param name="filter">The parsed filter, or null when every harness is selected.</param>
    /// <param name="error">Why the value was refused, or null when it parsed.</param>
    /// <returns>True when the value parsed.</returns>
    public static bool TryParse(string? value, out HarnessKind? filter, out string? error)
    {
        filter = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        string key = value.Trim();
        if (key.StartsWith('.'))
            key = key[1..];

        if (key.Equals("github", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("githubcopilot", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            filter = HarnessKind.GitHubCopilot;
            return true;
        }

        if (Enum.TryParse(key, ignoreCase: true, out HarnessKind parsed) &&
            parsed != HarnessKind.Custom)
        {
            filter = parsed;
            return true;
        }

        error = $"Unknown harness '{value}'. Expected: codex, cursor, claude, github, opencode, kilo.";
        return false;
    }

    /// <summary>Maps a harness folder name to its kind.</summary>
    /// <param name="folderName">The folder name, such as <c>.claude</c>.</param>
    /// <returns>The matching kind, or <see cref="HarnessKind.Custom"/> for an unknown folder.</returns>
    public static HarnessKind ForFolderName(string folderName) =>
        FolderToHarness.GetValueOrDefault(folderName, HarnessKind.Custom);
}
