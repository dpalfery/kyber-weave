using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Core.Parsing;
using YamlDotNet.Core;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// Which Claude hook sources would fire for one tool call: the project's settings files, the agent
/// file the payload's agent type names, and every effective skill. The user-level Arbiter hook asks
/// this before it evaluates a dispatch, so it can wait for a project hook that has the dispatch
/// instead of evaluating it a second time.
/// </summary>
/// <remarks>
/// Claude Code reads a hook from the container named by the event (<c>PreToolUse</c> or
/// <c>PostToolUse</c>) and fires it when that container's matcher matches the tool. A source that
/// does not carry the Arbiter's dispatch hook under the event's own container does not cover the
/// dispatch, so it must not make the user-level hook wait. Nothing here throws: an unreadable or
/// malformed file is a source that does not fire.
/// </remarks>
public static class ClaudeHookCoverage
{
    /// <summary>The command fragment that identifies a Claude hook running the Arbiter.</summary>
    public const string ArbiterClaudeHookMarker = "kyber-weave-arbiter hook --harness claude";

    /// <summary>The flag of the user-level hook. A command that carries it does not cover a dispatch.</summary>
    public const string GlobalScopeFlag = "--scope global";

    // A matcher is a .NET regex in this check. A pattern that stalls must not stall the dispatch.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // A hook source is a settings file or a short agent or skill file. One larger than this is not read.
    private const long MaxSourceBytes = 1024 * 1024;
    private const int MaxAgentTypeLength = 128;

    // An agent type names a file under .claude/agents. Anything else (a traversal, a plugin-qualified
    // name) names no file at all, so it cannot reach a decoy outside the agents folder.
    private static readonly Regex AgentTypePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The sources that exist for a payload, before any of them is read for its hooks. Use
    /// <paramref name="agentType"/> null to list the project's own sources, as the doctor does.
    /// </summary>
    /// <param name="projectRoot">The repository root that holds <c>.claude/</c>.</param>
    /// <param name="homeDirectory">The user's home directory, whose <c>.claude/</c> holds the personal agents and skills.</param>
    /// <param name="agentType">The payload's agent type, or null for a main-thread dispatch.</param>
    /// <returns>
    /// The relative path of each project source (for example <c>.claude/settings.json</c>), and the full
    /// path of each personal source, which lives outside the project.
    /// </returns>
    public static IReadOnlyList<string> EffectiveSources(string projectRoot, string homeDirectory, string? agentType)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(homeDirectory);

        return Collect(projectRoot, homeDirectory, agentType).Select(source => source.Display).ToList();
    }

    /// <summary>
    /// The sources whose hooks would fire for one tool call. A source counts only when its hooks map has
    /// the exact container named by <paramref name="eventName"/> holding an entry whose matcher matches
    /// <paramref name="toolName"/> and whose command runs the project-scope Arbiter hook.
    /// </summary>
    /// <param name="projectRoot">The repository root that holds <c>.claude/</c>.</param>
    /// <param name="homeDirectory">The user's home directory, whose <c>.claude/</c> holds the personal agents and skills.</param>
    /// <param name="agentType">The payload's agent type, or null for a main-thread dispatch.</param>
    /// <param name="eventName"><c>PreToolUse</c> or <c>PostToolUse</c>.</param>
    /// <param name="toolName">The tool the event is for, matched against each entry's matcher.</param>
    /// <returns>The display path of each source that would fire, in a stable order.</returns>
    public static IReadOnlyList<string> CandidateSources(
        string projectRoot,
        string homeDirectory,
        string? agentType,
        string eventName,
        string toolName)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(toolName);

        return Collect(projectRoot, homeDirectory, agentType)
            .Where(source => Fires(source.FullPath, eventName, toolName))
            .Select(source => source.Display)
            .ToList();
    }

    /// <summary>
    /// The project's own sources that would fire for one tool call under any agent type: the settings
    /// files, the effective skills, and each project agent file. The settings files and skills are read
    /// once, however many agent files there are.
    /// </summary>
    /// <param name="projectRoot">The repository root that holds <c>.claude/</c>.</param>
    /// <param name="homeDirectory">The user's home directory, whose <c>.claude/</c> holds the personal skills.</param>
    /// <param name="eventName"><c>PreToolUse</c> or <c>PostToolUse</c>.</param>
    /// <param name="toolName">The tool the event is for, matched against each entry's matcher.</param>
    /// <returns>The display path of each source that would fire, in a stable order.</returns>
    public static IReadOnlyList<string> ProjectCandidateSources(
        string projectRoot,
        string homeDirectory,
        string eventName,
        string toolName)
    {
        ArgumentNullException.ThrowIfNull(projectRoot);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(toolName);

        List<Source> sources = Collect(projectRoot, homeDirectory, null);
        string agents = Path.Combine(projectRoot, ".claude", "agents");
        try
        {
            if (Directory.Exists(agents))
            {
                foreach (string file in Directory.EnumerateFiles(agents, "*.md").Order(StringComparer.Ordinal))
                {
                    if (EffectiveAgent(projectRoot, homeDirectory, Path.GetFileNameWithoutExtension(file)) is { } agent)
                    {
                        sources.Add(agent);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An agents folder that cannot be listed contributes no agent-file hooks.
        }

        return sources
            .Where(source => Fires(source.FullPath, eventName, toolName))
            .Select(source => source.Display)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<Source> Collect(string projectRoot, string homeDirectory, string? agentType)
    {
        List<Source> sources = [];
        foreach (string settings in new[] { "settings.json", "settings.local.json" })
        {
            string path = Path.Combine(projectRoot, ".claude", settings);
            if (File.Exists(path))
            {
                sources.Add(new Source(Path.Combine(".claude", settings), path));
            }
        }

        Source? agent = EffectiveAgent(projectRoot, homeDirectory, agentType);
        if (agent is not null)
        {
            sources.Add(agent);
        }

        sources.AddRange(EffectiveSkills(projectRoot, homeDirectory));
        return sources;
    }

    // The project's agent file wins over the personal one of the same name, as Claude Code resolves it.
    private static Source? EffectiveAgent(string projectRoot, string homeDirectory, string? agentType)
    {
        if (agentType is null || !IsAgentType(agentType))
        {
            return null;
        }

        string fileName = agentType + ".md";
        string project = Path.Combine(projectRoot, ".claude", "agents", fileName);
        if (File.Exists(project))
        {
            return new Source(Path.Combine(".claude", "agents", fileName), project);
        }

        string personal = Path.Combine(homeDirectory, ".claude", "agents", fileName);
        return File.Exists(personal) ? new Source(personal, personal) : null;
    }

    // A personal skill shadows a project skill of the same name, so the personal SKILL.md is the one
    // that is effective when both exist.
    private static IEnumerable<Source> EffectiveSkills(string projectRoot, string homeDirectory)
    {
        Dictionary<string, Source> project = SkillFiles(projectRoot, personal: false);
        Dictionary<string, Source> personal = SkillFiles(homeDirectory, personal: true);
        foreach (string name in project.Keys.Union(personal.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            yield return personal.TryGetValue(name, out Source? shadowing)
                ? shadowing
                : project[name];
        }
    }

    private static Dictionary<string, Source> SkillFiles(string root, bool personal)
    {
        Dictionary<string, Source> skills = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileSystemInfo entry in ChildDirectories(Path.Combine(root, ".claude", "skills")))
        {
            string file = Path.Combine(entry.FullName, "SKILL.md");
            if (!File.Exists(file))
            {
                continue;
            }

            string display = personal
                ? file
                : Path.Combine(".claude", "skills", entry.Name, "SKILL.md");
            skills[entry.Name] = new Source(display, file);
        }

        return skills;
    }

    // One level only, and never a symbolic link: recursing would follow a link out of the skills folder.
    private static List<FileSystemInfo> ChildDirectories(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(path)
                .EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 })
                .Where(entry => entry is DirectoryInfo && entry.LinkTarget is null)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A skills folder that cannot be listed carries no hooks this check can see.
            return [];
        }
    }

    // A name this long is not an agent type, and a pattern that times out names no file either.
    private static bool IsAgentType(string agentType)
    {
        if (agentType.Length > MaxAgentTypeLength)
        {
            return false;
        }

        try
        {
            return AgentTypePattern.IsMatch(agentType);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // Reads at most MaxSourceBytes + 1 bytes, so a file that grows after it was listed still costs a bounded
    // read; one over the cap is not a hook source this check reads.
    private static string ReadBounded(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] buffer = new byte[MaxSourceBytes + 1];
        int length = 0;
        int read;
        while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
        {
            length += read;
        }

        if (length > MaxSourceBytes)
        {
            throw new InvalidDataException($"'{path}' is larger than {MaxSourceBytes} bytes.");
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static bool Fires(string path, string eventName, string toolName)
    {
        try
        {
            IReadOnlyList<HookGroup> groups = string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)
                ? FrontmatterGroups(path, eventName)
                : JsonGroups(path, eventName);
            return groups.Any(group => Matches(group.Matcher, toolName) && group.Commands.Any(IsArbiterClaudeHook));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or YamlException or InvalidDataException)
        {
            // A source that cannot be read or parsed does not fire. It must not stop the dispatch.
            return false;
        }
    }

    private static IReadOnlyList<HookGroup> JsonGroups(string path, string eventName)
    {
        JsonNode? root = JsonNode.Parse(ReadBounded(path));
        if (root is not JsonObject document
            || document["hooks"] is not JsonObject hooks
            || hooks[eventName] is not JsonArray containers)
        {
            return [];
        }

        List<HookGroup> groups = [];
        foreach (JsonNode? container in containers)
        {
            if (container is not JsonObject group)
            {
                continue;
            }

            List<string> commands = [];
            if (group["hooks"] is JsonArray actions)
            {
                foreach (JsonNode? action in actions)
                {
                    if (action is JsonObject entry && Text(entry["command"]) is { } command)
                    {
                        commands.Add(command);
                    }
                }
            }

            groups.Add(new HookGroup(Text(group["matcher"]), commands));
        }

        return groups;
    }

    private static IReadOnlyList<HookGroup> FrontmatterGroups(string path, string eventName)
    {
        FrontmatterReadResult frontmatter = MarkdownFrontmatterReader.Read(ReadBounded(path));
        if (!frontmatter.HasFrontmatter)
        {
            return [];
        }

        FrontmatterHooks? document = MarkdownFrontmatterReader.Deserializer.Deserialize<FrontmatterHooks>(frontmatter.Yaml);
        if (document?.Hooks is null || !document.Hooks.TryGetValue(eventName, out List<FrontmatterHookGroup>? containers))
        {
            return [];
        }

        List<HookGroup> groups = [];
        foreach (FrontmatterHookGroup? group in containers)
        {
            if (group is null)
            {
                continue;
            }

            List<string> commands = [];
            foreach (FrontmatterHookAction? action in group.Hooks ?? [])
            {
                if (action?.Command is { } command)
                {
                    commands.Add(command);
                }
            }

            groups.Add(new HookGroup(group.Matcher, commands));
        }

        return groups;
    }

    // An absent matcher matches every tool, as Claude Code treats it. The cost of a false match is a
    // bounded wait for a claim that never comes, so it errs toward waiting rather than toward a
    // double evaluation.
    private static bool Matches(string? matcher, string toolName)
    {
        try
        {
            return Regex.IsMatch(toolName, matcher ?? string.Empty, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException)
        {
            // A matcher that does not compile matches nothing.
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsArbiterClaudeHook(string command) =>
        command.Contains(ArbiterClaudeHookMarker, StringComparison.Ordinal)
        && !command.Contains(GlobalScopeFlag, StringComparison.Ordinal);

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private sealed record Source(string Display, string FullPath);

    private sealed record HookGroup(string? Matcher, IReadOnlyList<string> Commands);

    // Deserialized by YamlDotNet from a frontmatter block: only the hooks container is of interest.
    private sealed class FrontmatterHooks
    {
        public Dictionary<string, List<FrontmatterHookGroup>>? Hooks { get; set; }
    }

    private sealed class FrontmatterHookGroup
    {
        public string? Matcher { get; set; }

        public List<FrontmatterHookAction>? Hooks { get; set; }
    }

    private sealed class FrontmatterHookAction
    {
        public string? Command { get; set; }
    }
}
