using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Inserts the any-repo <c>kw-docs</c> server into Antigravity's user MCP config on a global deployment, so the
/// agents Squad deploys there reach the documentation tools in every initialized repository.
/// </summary>
/// <remarks>
/// <para>
/// The file is the operator's: it can hold other servers, their API keys, comments and a byte-order mark. So the
/// change is one byte splice through <see cref="JsonServerSplice.Plan"/>, written only when the splice inserts.
/// An existing <c>kw-docs</c> entry is never modified, a document the splice cannot read is refused, and nothing
/// is ever re-serialized.
/// </para>
/// <para>
/// A warning carries the path and fixed text only. File bytes, property values, the splice's message and
/// exception messages are all left out, because a <see cref="System.Text.Json"/> reader message can quote the
/// offending character and the file can hold another server's secret.
/// </para>
/// </remarks>
public static class AntigravityMcpConfiguration
{
    /// <summary>The prefix on every line this class reports, matching the other lifecycle warnings' code form.</summary>
    public const string WarningCode = "antigravity-mcp-config";

    /// <summary>The largest file the config is read from; a larger one is refused before it is loaded.</summary>
    public const long MaxConfigBytes = 1024 * 1024;

    private const string ConfigFileName = "mcp_config.json";
    private const string ServersKey = "mcpServers";
    private const string ServerName = "kw-docs";

    private static readonly byte[] EmptyDocument = "{}\n"u8.ToArray();
    private static readonly string[] ServerKeyPath = [ServersKey];

    private static readonly JsonDocumentOptions LenientRead = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Inserts the <c>kw-docs</c> entry into <c>mcp_config.json</c> under Antigravity's global root when a global
    /// deployment targets Antigravity, and returns the lines to report.
    /// </summary>
    /// <param name="files">The file probes the config is read and written through.</param>
    /// <param name="targets">The harnesses this deployment renders.</param>
    /// <param name="scope">The deployment scope; only a global deployment owns the user config.</param>
    /// <param name="globalRoots">Resolves Antigravity's global root, or null when the deployment has none.</param>
    /// <param name="docsServerCommand">
    /// The command the entry launches, or null (or blank) for <c>kyber-weave-mcp</c> found on the path.
    /// </param>
    /// <param name="dryRun">True to report what would be added and write nothing.</param>
    /// <returns>
    /// Nothing when the entry was inserted, is already present with <c>--any-repo</c>, or the deployment is out of
    /// scope; otherwise one line naming the file and why it was left alone, or the dry run's note.
    /// </returns>
    public static IReadOnlyList<string> Ensure(
        ISquadFileAccess files,
        IReadOnlyCollection<SquadTarget> targets,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        string? docsServerCommand,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(targets);

        if (scope != SquadDeploymentScope.Global || !targets.Contains(SquadTarget.Antigravity) || globalRoots is null)
        {
            return [];
        }

        string path = Path.Combine(globalRoots.ResolveGlobalRoot(SquadTarget.Antigravity), ConfigFileName);

        byte[] document;
        try
        {
            if (!files.FileExists(path))
            {
                document = EmptyDocument;
            }
            else if (files.GetLength(path) > MaxConfigBytes)
            {
                return [Line(path, "is larger than 1 MiB, so the kw-docs MCP server was not added to it.")];
            }
            else
            {
                document = files.ReadAllBytes(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [Line(path, "could not be read, so the kw-docs MCP server was not added to it.")];
        }

        JsonSplicePlan plan = JsonServerSplice.Plan(document, ServerKeyPath, ServerName, EntryJson(docsServerCommand));

        switch (plan.Outcome)
        {
            case JsonSpliceOutcome.Inserted:
                return dryRun
                    ? [$"{WarningCode}: would add the kw-docs MCP server to '{SquadLifecycleService.SanitizeTargetTokenForWarning(path)}'."]
                    : Write(files, path, plan.Document);
            case JsonSpliceOutcome.ServerAlreadyPresent:
                return ExistingEntryWarnings(path, document);
            case JsonSpliceOutcome.MalformedDocument:
                return [Line(path, "is not valid JSON, so the kw-docs MCP server was not added to it.")];
            case JsonSpliceOutcome.ForeignDocument:
                return [Line(path, "is not a JSON object with an mcpServers object, so the kw-docs MCP server was not added to it.")];
            default:
                return [Line(path, "could not be changed safely, so the kw-docs MCP server was not added to it.")];
        }
    }

    /// <remarks>Built as a node so the command is escaped by <see cref="System.Text.Json"/>, never by hand.</remarks>
    private static string EntryJson(string? docsServerCommand)
    {
        JsonObject entry = new()
        {
            ["command"] = string.IsNullOrWhiteSpace(docsServerCommand) ? SquadDocsServerCommand.Executable : docsServerCommand,
            ["args"] = new JsonArray(SquadDocsServerCommand.AnyRepoArgument)
        };
        return entry.ToJsonString();
    }

    private static IReadOnlyList<string> Write(ISquadFileAccess files, string path, byte[] contents)
    {
        try
        {
            files.WriteAllBytesAtomic(path, contents);
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [Line(path, "could not be written, so the kw-docs MCP server was not added to it.")];
        }
    }

    /// <summary>
    /// Warns once when the existing entry does not pass <c>--any-repo</c>. The entry is the operator's, so it is
    /// reported, never rewritten; its value is never quoted.
    /// </summary>
    /// <remarks>
    /// The read is as lenient as <see cref="JsonServerSplice"/>'s — byte-order mark stripped, comments skipped,
    /// trailing commas allowed — so a file the splice found the entry in is a file this check can read.
    /// </remarks>
    private static IReadOnlyList<string> ExistingEntryWarnings(string path, byte[] document)
    {
        try
        {
            ReadOnlyMemory<byte> json = document.AsMemory();
            if (json.Span.StartsWith(Encoding.UTF8.Preamble))
            {
                json = json[Encoding.UTF8.Preamble.Length..];
            }

            using JsonDocument parsed = JsonDocument.Parse(json, LenientRead);
            if (HasAnyRepoArgument(parsed.RootElement))
            {
                return [];
            }

            return [Line(path, "already defines a kw-docs MCP server that does not pass --any-repo, and it was left " +
                               "unchanged. Add --any-repo to its args so Antigravity reaches every repository.")];
        }
        catch (JsonException)
        {
            return [Line(path, "already defines a kw-docs MCP server whose args could not be read, and it was left unchanged.")];
        }
    }

    private static bool HasAnyRepoArgument(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(ServersKey, out JsonElement servers)
        && servers.ValueKind == JsonValueKind.Object
        && servers.TryGetProperty(ServerName, out JsonElement entry)
        && entry.ValueKind == JsonValueKind.Object
        && entry.TryGetProperty("args", out JsonElement args)
        && args.ValueKind == JsonValueKind.Array
        && args.EnumerateArray().Any(argument =>
            argument.ValueKind == JsonValueKind.String && argument.ValueEquals(SquadDocsServerCommand.AnyRepoArgument));

    private static string Line(string path, string reason) =>
        $"{WarningCode}: '{SquadLifecycleService.SanitizeTargetTokenForWarning(path)}' {reason}";
}
