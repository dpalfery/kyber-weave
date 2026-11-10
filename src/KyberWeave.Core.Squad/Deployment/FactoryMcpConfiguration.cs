using System.Text.Json;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Warns when Factory droids are allowed MCP servers that no Factory config file defines.
/// </summary>
/// <remarks>
/// A droid's <c>mcpServers</c> list only filters servers Factory has already started, and Squad
/// writes no Factory MCP config, so a missing definition leaves every droid without those tools
/// and with no error. Factory reads <c>~/.factory/mcp.json</c> and <c>.factory/mcp.json</c> in the
/// project and its ancestor directories (docs.factory.com/harness/mcp, checked 2026-11-10). The
/// walk does not stop at a repository boundary: Factory documents no such stop, and a config
/// shared by several repositories under a common parent is its stated use for that level.
/// </remarks>
public static class FactoryMcpConfiguration
{
    /// <summary>The prefix on the warning, matching the other lifecycle warnings' code form.</summary>
    public const string WarningCode = "factory-mcp-not-configured";

    private const string ProjectConfigDirectory = ".factory";
    private const string ConfigFileName = "mcp.json";
    private const string ServersKey = "mcpServers";

    /// <summary>
    /// Returns one warning naming the granted servers Factory has no definition for, or nothing
    /// when Factory is not a target, every server is defined, or the scope has no known config path.
    /// </summary>
    /// <param name="targets">The harnesses this deployment renders.</param>
    /// <param name="scope">The deployment scope; a harness instance's config location is not known.</param>
    /// <param name="source">The squad source whose <c>mcp.json</c> names the granted servers.</param>
    /// <param name="targetRoot">The project root for a project deployment.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    public static IReadOnlyList<string> Warnings(
        IReadOnlyCollection<SquadTarget> targets,
        SquadDeploymentScope scope,
        SquadSource? source,
        string targetRoot,
        string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        if (source is null || !targets.Contains(SquadTarget.Factory) || scope == SquadDeploymentScope.Instance)
        {
            return [];
        }

        List<string> unreadable = [];
        HashSet<string> configured = new(
            ConfigPaths(scope, targetRoot, homeDirectory)
                .Distinct(StringComparer.Ordinal)
                .SelectMany(path => ReadServerNames(path, unreadable))
                .ToList(),
            StringComparer.Ordinal);
        string[] missing =
        [
            .. FactoryRenderer.DeclaredMcpServerNames(source).Where(server => !configured.Contains(server))
        ];
        if (missing.Length == 0)
        {
            return [];
        }

        string servers = string.Join(", ", missing.Select(server => Describe(source, server)));
        string remedy = scope == SquadDeploymentScope.Global
            ? "~/.factory/mcp.json does not define them, so droids get those tools only in projects " +
              "whose own .factory/mcp.json does. Define them in one of those files."
            : "neither .factory/mcp.json, here or in a parent folder, nor ~/.factory/mcp.json defines " +
              "them, so droids will not get those tools. Define them in one of those files.";
        string broken = unreadable.Count == 0
            ? string.Empty
            : $" Fix these unreadable files first: {string.Join("; ", unreadable)}.";

        return
        [
            SquadLifecycleService.SanitizeTargetTokenForWarning(
                $"{WarningCode}: Factory droids are allowed the MCP servers {servers}, but {remedy}{broken}")
        ];
    }

    private static IEnumerable<string> ConfigPaths(
        SquadDeploymentScope scope,
        string targetRoot,
        string homeDirectory)
    {
        yield return Path.Combine(homeDirectory, ProjectConfigDirectory, ConfigFileName);

        if (scope != SquadDeploymentScope.Project)
        {
            yield break;
        }

        for (DirectoryInfo? directory = new(targetRoot); directory is not null; directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, ProjectConfigDirectory, ConfigFileName);
        }
    }

    private static IReadOnlyList<string> ReadServerNames(string path, List<string> unreadable)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                unreadable.Add($"{path} (the top level is not a JSON object)");
                return [];
            }

            if (!document.RootElement.TryGetProperty(ServersKey, out JsonElement servers))
            {
                return [];
            }

            if (servers.ValueKind != JsonValueKind.Object)
            {
                unreadable.Add($"{path} ({ServersKey} is not a JSON object)");
                return [];
            }

            return [.. servers.EnumerateObject().Select(server => server.Name)];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{path} ({exception.Message})");
            return [];
        }
    }

    private static string Describe(SquadSource source, string server)
    {
        JsonElement entry = source.McpConfiguration.GetProperty(ServersKey).GetProperty(server);
        if (entry.ValueKind != JsonValueKind.Object ||
            !entry.TryGetProperty("command", out JsonElement command) ||
            command.ValueKind != JsonValueKind.String)
        {
            return server;
        }

        IEnumerable<string> arguments =
            entry.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Array
                ? args.EnumerateArray().Select(argument => argument.ToString())
                : [];

        return $"{server} ({string.Join(' ', arguments.Prepend(command.GetString()!))})";
    }
}
