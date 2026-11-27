using System.Text.RegularExpressions;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.FileSystem;
using YamlDotNet.Core;

namespace KyberWeave.Application.Agents;

/// <summary>Discovers and loads agent definitions from a project root by convention.</summary>
/// <remarks>
/// Every <c>.harnessname/agents</c> directory under the root is a harness agent tree. The
/// parser it dispatches to is pure; reading the files and listing the directories happens here.
/// </remarks>
public sealed class AgentFileReader(IFileSystem files, IPathResolver paths, IProcessEnvironment environment)
{
    // Held through the interface, and in priority order: TOML first, because a .agent.md
    // file matches the Markdown parser and nothing else, while the TOML parser is the
    // narrower predicate. Dispatching through IAgentParser is what stops that interface
    // from being an abstraction with two implementations and no consumer.
    private readonly IAgentParser[] _parsers =
        [new TomlAgentParser(paths, environment), new MarkdownAgentParser(paths, environment)];

    /// <summary>Loads every agent definition under a project root.</summary>
    /// <param name="projectRoot">The project root.</param>
    /// <param name="harnessFilter">When set, only this harness's definitions are loaded.</param>
    /// <returns>The agents that parsed.</returns>
    public AgentSet LoadAll(string projectRoot, HarnessKind? harnessFilter = null)
    {
        IReadOnlyList<AgentLoadResult> results = LoadResults(projectRoot, harnessFilter);
        List<AgentModel> validAgents = results
            .Where(r => r is { Success: true, Agent: not null })
            .Select(r => r.Agent!)
            .ToList();
        return new AgentSet(validAgents);
    }

    /// <summary>Finds every <c>.*/agents</c> directory under the project root.</summary>
    /// <param name="projectRoot">The project root.</param>
    /// <returns>The directories, ordered by harness folder name.</returns>
    public IReadOnlyList<(string AgentsDir, HarnessKind Kind, string HarnessFolder)> DiscoverHarnessAgentDirs(
        string projectRoot)
    {
        List<(string AgentsDir, HarnessKind Kind, string HarnessFolder)> found = [];

        foreach (string dir in files.EnumerateDirectories(projectRoot))
        {
            string folderName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(folderName) || folderName[0] != '.')
                continue;

            string agentsDir = Path.Combine(dir, "agents");
            if (!files.DirectoryExists(agentsDir))
                continue;

            found.Add((agentsDir, AgentHarnessFilter.ForFolderName(folderName), folderName));
        }

        return found
            .OrderBy(x => x.HarnessFolder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<AgentLoadResult> LoadResults(string projectRoot, HarnessKind? harnessFilter = null)
    {
        List<AgentLoadResult> results = [];
        string fullRoot = paths.GetFullPath(projectRoot, environment.CurrentDirectory);

        if (!files.DirectoryExists(fullRoot))
            return results;

        foreach ((string agentsDir, HarnessKind kind, string _) in DiscoverHarnessAgentDirs(fullRoot))
        {
            if (harnessFilter is not null && kind != harnessFilter.Value)
                continue;

            LoadHarnessDirectory(agentsDir, kind, results);
        }

        return results;
    }

    private void LoadHarnessDirectory(string harnessDir, HarnessKind harness, List<AgentLoadResult> results)
    {
        IEnumerable<string> agentFiles = files
            .EnumerateFiles(harnessDir, "*", recursive: false)
            .Where(f => f.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase));

        foreach (string file in agentFiles)
        {
            try
            {
                IAgentParser? parser = Array.Find(_parsers, p => p.CanParse(file));
                if (parser is null)
                    continue;

                results.Add(new AgentLoadResult(true, parser.Parse(files.ReadAllText(file), file, harness), file, null));
            }
            catch (Exception ex) when (FileFailure.Is(ex) ||
                                       ex is InvalidOperationException or FormatException or RegexMatchTimeoutException or YamlException)
            {
                results.Add(new AgentLoadResult(false, null, file, ex.Message));
            }
        }
    }
}
