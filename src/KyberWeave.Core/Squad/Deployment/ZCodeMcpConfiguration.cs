using System.Text.Json;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The outcome of checking a ZCode installation for the MCP servers Squad requires.</summary>
/// <param name="ConfiguredServers">Server names declared across every ZCode config tier.</param>
/// <param name="MissingServers">Required servers no tier declares, in stable order.</param>
/// <param name="InspectedPaths">Config paths that existed and were read, in precedence order.</param>
public sealed record ZCodeMcpConfigurationReport(
    IReadOnlyList<string> ConfiguredServers,
    IReadOnlyList<string> MissingServers,
    IReadOnlyList<string> InspectedPaths);

/// <summary>
/// Reads the MCP servers a ZCode installation declares, so <c>squad doctor</c> can fail before
/// a deployment that needs them does.
/// </summary>
/// <remarks>
/// <para>
/// <c>ZCodeRenderer</c> grants MCP by fully qualified tool name, because ZCode's
/// <c>registerMcpTools</c> matches an allow-list by exact name and expands no wildcard. That
/// makes the grant real, and it also makes it a hard requirement:
/// <c>validateSubagentMcpRequirements</c> raises a configuration error when a required tool's
/// server is not connected, so a Squad agent on a ZCode install without these servers fails
/// rather than degrading. This check is the other half of that trade — the failure surfaces at
/// diagnosis time instead of mid-run.
/// </para>
/// <para>
/// Config tiers, verified against zai-org/ZCode 3.14.0 on 2026-09-21: the user file at
/// <c>&lt;storage&gt;/cli/config.json</c>, and per-directory project files at
/// <c>&lt;dir&gt;/zcode.json</c> and <c>&lt;dir&gt;/.zcode/config.json</c> for every directory
/// from the working directory up to the git worktree root
/// (<c>buildWorkspaceHookCandidatePaths</c> over <c>getProjectConfigDirectories</c>). Servers
/// live under <c>mcp.servers</c> (<c>mcpSchema</c>). A server declared in any tier counts:
/// this reports what ZCode can see, not which tier wins, because precedence only matters when
/// two tiers define the same server differently.
/// </para>
/// </remarks>
public static class ZCodeMcpConfiguration
{
    private const string WorktreeMarker = ".git";

    /// <summary>
    /// Reports which required MCP servers a ZCode installation declares and which it does not.
    /// </summary>
    /// <param name="requiredServers">Server names the canonical toolchain declares.</param>
    /// <param name="zcodeStorageRoot">The resolved ZCode storage root.</param>
    /// <param name="workingDirectory">The directory a ZCode session would start in.</param>
    /// <param name="readFileText">Reads a file's text, or null when absent or unreadable.</param>
    public static ZCodeMcpConfigurationReport Inspect(
        IReadOnlyCollection<string> requiredServers,
        string zcodeStorageRoot,
        string workingDirectory,
        Func<string, string?> readFileText)
    {
        ArgumentNullException.ThrowIfNull(requiredServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(zcodeStorageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(readFileText);

        SortedSet<string> configured = new(StringComparer.Ordinal);
        List<string> inspected = [];

        foreach (string path in CandidatePaths(zcodeStorageRoot, workingDirectory))
        {
            string? text = readFileText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            inspected.Add(path);
            foreach (string server in ReadServerNames(text))
            {
                configured.Add(server);
            }
        }

        IReadOnlyList<string> missing =
        [
            .. requiredServers
                .Where(server => !configured.Contains(server))
                .OrderBy(server => server, StringComparer.Ordinal)
        ];

        return new ZCodeMcpConfigurationReport([.. configured], missing, inspected);
    }

    private static IEnumerable<string> CandidatePaths(string zcodeStorageRoot, string workingDirectory)
    {
        yield return Path.Combine(zcodeStorageRoot, "cli", "config.json");

        foreach (string directory in ProjectConfigDirectories(workingDirectory))
        {
            yield return Path.Combine(directory, "zcode.json");
            yield return Path.Combine(directory, ".zcode", "config.json");
        }
    }

    /// <summary>
    /// Walks from the working directory up to the git worktree root, matching ZCode's own
    /// <c>getProjectConfigDirectories</c>. A directory tree with no worktree marker contributes
    /// only the starting directory, as it does there.
    /// </summary>
    private static IReadOnlyList<string> ProjectConfigDirectories(string workingDirectory)
    {
        string start = Path.GetFullPath(workingDirectory);
        List<string> directories = [];
        string current = start;
        while (true)
        {
            directories.Add(current);
            if (Path.Exists(Path.Combine(current, WorktreeMarker)))
            {
                return directories;
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return [start];
            }

            current = parent;
        }
    }

    /// <summary>
    /// Reads <c>mcp.servers</c> keys. A malformed or unexpected document contributes nothing
    /// rather than throwing: a doctor check that crashes on a config it cannot parse is worse
    /// than one that reports the servers it could find.
    /// </summary>
    private static IEnumerable<string> ReadServerNames(string text)
    {
        JsonElement servers;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("mcp", out JsonElement mcp) ||
                mcp.ValueKind != JsonValueKind.Object ||
                !mcp.TryGetProperty("servers", out servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            servers = servers.Clone();
        }
        catch (JsonException)
        {
            yield break;
        }

        foreach (JsonProperty server in servers.EnumerateObject())
        {
            yield return server.Name;
        }
    }
}
