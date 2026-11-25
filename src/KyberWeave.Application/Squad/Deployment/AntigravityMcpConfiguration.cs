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
/// Antigravity, or the operator, can edit the file while Squad updates it, and a plan applied to stale bytes would
/// put back the document as first read and drop their change. So the file is read again just before the write, and
/// the write happens only when it still holds the bytes the plan was made from. When it changed, the change is
/// planned once more from the new bytes; when it changed again, the file is left alone with a warning. That narrows
/// the window to the rename itself; it does not close it, because the seam offers no lock the editor also takes.
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

    /// <summary>
    /// How many times the change is planned: once from the first read, and once more from the bytes another program
    /// wrote in between, before the update gives up and leaves the file alone.
    /// </summary>
    private const int MaxPlans = 2;

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
        string entryJson = EntryJson(docsServerCommand);

        ConfigRead current = Read(files, path);
        for (int attempt = 0; attempt < MaxPlans; attempt++)
        {
            if (current.Refusal is not null)
            {
                return [current.Refusal];
            }

            byte[] document = current.Document ?? EmptyDocument;
            JsonSplicePlan plan = JsonServerSplice.Plan(document, ServerKeyPath, ServerName, entryJson);

            switch (plan.Outcome)
            {
                case JsonSpliceOutcome.Inserted when dryRun:
                    return [$"{WarningCode}: would add the kw-docs MCP server to '{SquadLifecycleService.SanitizeTargetTokenForWarning(path)}'."];
                case JsonSpliceOutcome.Inserted:
                    ConfigRead latest = Read(files, path);
                    if (latest.Refusal is null && latest.SameAs(current))
                    {
                        return Write(files, path, plan.Document);
                    }

                    current = latest;
                    continue;
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

        return current.Refusal is not null
            ? [current.Refusal]
            : [Line(path, "changed while it was being updated, so the kw-docs MCP server was not added to it. Run the command again.")];
    }

    /// <summary>
    /// Reads the config, or notes it is missing, or says why it was refused: larger than <see cref="MaxConfigBytes"/>
    /// or unreadable.
    /// </summary>
    /// <remarks>
    /// The length probe refuses an oversized file before it is loaded, but the file can grow between the probe and the
    /// read, so the cap is checked again on the bytes the read returned.
    /// </remarks>
    private static ConfigRead Read(ISquadFileAccess files, string path)
    {
        try
        {
            if (!files.FileExists(path))
            {
                return new ConfigRead(null, null);
            }

            if (files.GetLength(path) > MaxConfigBytes)
            {
                return new ConfigRead(null, TooLarge(path));
            }

            byte[] document = files.ReadAllBytes(path);
            return document.LongLength > MaxConfigBytes
                ? new ConfigRead(null, TooLarge(path))
                : new ConfigRead(document, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ConfigRead(null, Line(path, "could not be read, so the kw-docs MCP server was not added to it."));
        }
    }

    private static string TooLarge(string path) =>
        Line(path, "is larger than 1 MiB, so the kw-docs MCP server was not added to it.");

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

    /// <summary>One read of the config: its bytes, or null when it does not exist, or the line saying why it was refused.</summary>
    /// <param name="Document">The file's bytes, or null when the file does not exist or was refused.</param>
    /// <param name="Refusal">The warning when the file is too large or unreadable, otherwise null.</param>
    private sealed record ConfigRead(byte[]? Document, string? Refusal)
    {
        /// <summary>True when both reads saw a missing file, or the same bytes.</summary>
        public bool SameAs(ConfigRead other) =>
            Document is null
                ? other.Document is null
                : other.Document is not null && Document.AsSpan().SequenceEqual(other.Document);
    }
}
