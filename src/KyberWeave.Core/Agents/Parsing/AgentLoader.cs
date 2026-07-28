using KyberWeave.Core.Agents.Model;

namespace KyberWeave.Core.Agents.Parsing;

/// <summary>
/// Result of attempting to load a single agent file.
/// </summary>
public sealed record AgentLoadResult(bool Success, AgentModel? Agent, string Path, string? Error);

/// <summary>
/// Discovers and loads agent definitions from a project root by convention:
/// every <c>.harnessname/agents</c> directory under the root is a harness agent tree.
/// </summary>
public static class AgentLoader
{
    private static readonly TomlAgentParser TomlParser = new();
    private static readonly MarkdownAgentParser MarkdownParser = new();

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

    public static AgentSet LoadAll(string projectRoot, HarnessKind? harnessFilter = null)
    {
        var results = LoadResults(projectRoot, harnessFilter);
        var validAgents = results.Where(r => r.Success && r.Agent is not null).Select(r => r.Agent!).ToList();
        return new AgentSet(validAgents);
    }

    public static List<AgentLoadResult> LoadResults(string projectRoot, HarnessKind? harnessFilter = null)
    {
        var results = new List<AgentLoadResult>();
        var fullRoot = Path.GetFullPath(projectRoot);

        if (!Directory.Exists(fullRoot))
            return results;

        foreach (var (agentsDir, kind, _) in DiscoverHarnessAgentDirs(fullRoot))
        {
            if (harnessFilter is not null && kind != harnessFilter.Value)
                continue;

            LoadHarnessDirectory(agentsDir, kind, results);
        }

        return results;
    }

    /// <summary>
    /// Parses a CLI harness filter such as <c>cursor</c>, <c>github</c>, or <c>GitHubCopilot</c>.
    /// Empty/null means all discovered harnesses.
    /// </summary>
    public static bool TryParseHarnessFilter(string? value, out HarnessKind? filter, out string? error)
    {
        filter = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        var key = value.Trim();
        if (key.StartsWith('.'))
            key = key[1..];

        if (key.Equals("github", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("githubcopilot", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("copilot", StringComparison.OrdinalIgnoreCase))
        {
            filter = HarnessKind.GitHubCopilot;
            return true;
        }

        if (Enum.TryParse<HarnessKind>(key, ignoreCase: true, out var parsed) &&
            parsed != HarnessKind.Custom)
        {
            filter = parsed;
            return true;
        }

        error = $"Unknown harness '{value}'. Expected: codex, cursor, claude, github, opencode, kilo.";
        return false;
    }

    /// <summary>
    /// Finds every <c>.*/agents</c> directory under the project root.
    /// Known folder names map to <see cref="HarnessKind"/>; others map to <see cref="HarnessKind.Custom"/>.
    /// </summary>
    public static IReadOnlyList<(string AgentsDir, HarnessKind Kind, string HarnessFolder)> DiscoverHarnessAgentDirs(
        string projectRoot)
    {
        var found = new List<(string, HarnessKind, string)>();

        foreach (var dir in Directory.EnumerateDirectories(projectRoot))
        {
            var folderName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(folderName) || folderName[0] != '.')
                continue;

            var agentsDir = Path.Combine(dir, "agents");
            if (!Directory.Exists(agentsDir))
                continue;

            var kind = FolderToHarness.TryGetValue(folderName, out var mapped)
                ? mapped
                : HarnessKind.Custom;

            found.Add((agentsDir, kind, folderName));
        }

        return found
            .OrderBy(x => x.Item3, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void LoadHarnessDirectory(
        string harnessDir,
        HarnessKind harness,
        List<AgentLoadResult> results)
    {
        var files = Directory.GetFiles(harnessDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            try
            {
                AgentModel agent;
                if (TomlParser.CanParse(file))
                    agent = TomlParser.Parse(file, harness);
                else if (MarkdownParser.CanParse(file))
                    agent = MarkdownParser.Parse(file, harness);
                else
                    continue;

                results.Add(new AgentLoadResult(true, agent, file, null));
            }
            catch (Exception ex)
            {
                results.Add(new AgentLoadResult(false, null, file, ex.Message));
            }
        }
    }
}
