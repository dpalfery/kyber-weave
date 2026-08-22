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
    // Held through the interface, and in priority order: TOML first, because a .agent.md
    // file matches the Markdown parser and nothing else, while the TOML parser is the
    // narrower predicate. Dispatching through IAgentParser is what stops that interface
    // from being an abstraction with two implementations and no consumer.
    private static readonly IAgentParser[] Parsers = [new TomlAgentParser(), new MarkdownAgentParser()];

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
        IReadOnlyList<AgentLoadResult> results = LoadResults(projectRoot, harnessFilter);
        List<AgentModel> validAgents = results.Where(r => r is { Success: true, Agent: not null }).Select(r => r.Agent!).ToList();
        return new AgentSet(validAgents);
    }

    private static IReadOnlyList<AgentLoadResult> LoadResults(string projectRoot, HarnessKind? harnessFilter = null)
    {
        List<AgentLoadResult> results = new List<AgentLoadResult>();
        string fullRoot = Path.GetFullPath(projectRoot);

        if (!Directory.Exists(fullRoot))
            return results;

        foreach ((string? agentsDir, HarnessKind kind, string _) in DiscoverHarnessAgentDirs(fullRoot))
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

    /// <summary>
    /// Finds every <c>.*/agents</c> directory under the project root.
    /// Known folder names map to <see cref="HarnessKind"/>; others map to <see cref="HarnessKind.Custom"/>.
    /// </summary>
    public static IReadOnlyList<(string AgentsDir, HarnessKind Kind, string HarnessFolder)> DiscoverHarnessAgentDirs(
        string projectRoot)
    {
        List<(string, HarnessKind, string)> found = new List<(string, HarnessKind, string)>();

        foreach (string dir in Directory.EnumerateDirectories(projectRoot))
        {
            string folderName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(folderName) || folderName[0] != '.')
                continue;

            string agentsDir = Path.Combine(dir, "agents");
            if (!Directory.Exists(agentsDir))
                continue;

            HarnessKind kind = FolderToHarness.GetValueOrDefault(folderName, HarnessKind.Custom);

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
        IEnumerable<string> files = Directory.GetFiles(harnessDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase));

        foreach (string file in files)
        {
            try
            {
                IAgentParser? parser = Array.Find(Parsers, p => p.CanParse(file));
                if (parser is null)
                    continue;

                results.Add(new AgentLoadResult(true, parser.Parse(file, harness), file, null));
            }
            catch (Exception ex)
            {
                results.Add(new AgentLoadResult(false, null, file, ex.Message));
            }
        }
    }
}
