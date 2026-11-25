using System.Globalization;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;

namespace KyberDash.Data.Harnesses.Cursor;

/// <summary>
/// Reads Cursor's <c>cursorDiskKV</c> bubble rows from <c>state.vscdb</c> as one conversation per composer.
/// </summary>
/// <remarks>
/// A composer is found from its bubble keys (<c>bubbleId:&lt;composerId&gt;:&lt;bubbleId&gt;</c>) by a key-only
/// range scan over the unique key index, so discovery never reads a bubble value, and a composer whose
/// <c>composerData:</c> row is missing is still listed. There is no time filter: the table has no activity
/// column, and the revision token (bubble count and largest rowid) lets an unchanged composer be skipped
/// after the first pass. The export is the D10 array that <see cref="CursorMapper"/> reads; a bubble
/// without a value is written as JSON <c>null</c>, never a default (D9).
/// </remarks>
public sealed class CursorSqliteConversationSource : ISqliteConversationSource
{
    private const string BubblePrefix = "bubbleId:";
    private const string BubbleUpperBound = "bubbleId;";
    private const string ComposersQueryName = "cursor-composers";
    private const string BubblesQueryName = "cursor-bubbles";
    private const string WorkspaceComposersQueryName = "cursor-workspace-composers";
    private const string WorkspaceStorageFolder = "workspaceStorage";
    private const string WorkspaceDatabaseName = "state.vscdb";
    private const string WorkspaceFileName = "workspace.json";
    private const string WorkspaceProblemCode = "CURSOR_WORKSPACE_UNREADABLE";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredColumns { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["cursorDiskKV"] = ["key", "value"],
        };

    /// <inheritdoc />
    /// <remarks>
    /// The composer id is the text between the second and third <c>:</c> of a bubble key, so a key with no
    /// second colon, or an empty composer id, is not a composer and is left out of the scan.
    /// </remarks>
    public IEnumerable<SqliteConversation> ListConversations(SqliteConversationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        HarnessSqliteQuery query = new(
            ComposersQueryName,
            """
            SELECT substr(key, 10, instr(substr(key, 10), ':') - 1) AS composer_id,
                   COUNT(*) AS bubble_count,
                   MAX(rowid) AS max_rowid
            FROM cursorDiskKV
            WHERE key >= ? AND key < ? AND instr(substr(key, 10), ':') > 1
            GROUP BY composer_id
            ORDER BY composer_id
            """,
            [BubblePrefix, BubbleUpperBound]);

        Dictionary<string, string> workspaceByComposer = BuildWorkspaceMap(context);
        List<SqliteConversation> conversations = [];
        foreach (IReadOnlyDictionary<string, object?> row in context.Database.Query(query))
        {
            string composerId = ReadText(row, "composer_id");
            long bubbleCount = ReadInteger(row, "bubble_count");
            long maxRowId = ReadInteger(row, "max_rowid");
            conversations.Add(new SqliteConversation(
                Id: composerId,
                RevisionToken: string.Create(CultureInfo.InvariantCulture, $"{bubbleCount}-{maxRowId}"),
                LastActivityUtc: null,
                CompanionKey: workspaceByComposer.GetValueOrDefault(composerId)));
        }

        return conversations;
    }

    /// <summary>
    /// Maps each composer id to the <c>workspace.json</c> of the workspace that claims it (D14). The map is
    /// built once per listing from every <c>Cursor/User/workspaceStorage/&lt;hash&gt;/state.vscdb</c>, whose
    /// <c>ItemTable</c> row <c>composer.composerData</c> lists <c>allComposers[].composerId</c>. A workspace
    /// database that cannot be opened, has no such row, or holds malformed JSON adds one warning and is skipped.
    /// </summary>
    private static Dictionary<string, string> BuildWorkspaceMap(SqliteConversationContext context)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        string? userDirectory = Path.GetDirectoryName(Path.GetDirectoryName(context.DatabasePath));
        if (string.IsNullOrEmpty(userDirectory))
        {
            return map;
        }

        string storageRoot = Path.Combine(userDirectory, WorkspaceStorageFolder);
        if (!context.FileSystem.DirectoryExists(storageRoot))
        {
            return map;
        }

        HarnessSqliteQuery query = new(
            WorkspaceComposersQueryName,
            "SELECT value FROM ItemTable WHERE key = 'composer.composerData'",
            []);

        foreach (string hashDirectory in context.FileSystem.EnumerateDirectories(storageRoot).Order(StringComparer.Ordinal))
        {
            string databasePath = Path.Combine(hashDirectory, WorkspaceDatabaseName);
            if (!context.FileSystem.FileExists(databasePath))
            {
                continue;
            }

            string workspaceFile = Path.Combine(hashDirectory, WorkspaceFileName);
            string? companionKey = context.FileSystem.FileExists(workspaceFile) ? workspaceFile : null;
            try
            {
                IReadOnlyList<string> composerIds = ReadWorkspaceComposerIds(context.Sqlite, databasePath, query);
                if (companionKey is null)
                {
                    continue;
                }

                foreach (string composerId in composerIds)
                {
                    // The first workspace to claim a composer keeps it, so a map is stable across runs.
                    map.TryAdd(composerId, companionKey);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or InvalidOperationException)
            {
                context.Problems.Add(new CollectedProblem(
                    ProblemKey: $"cursor-workspace:{databasePath}",
                    Severity: "warning",
                    Code: WorkspaceProblemCode,
                    Message: $"Skipping Cursor workspace database {databasePath}: {ex.Message}",
                    Location: databasePath,
                    HarnessId: context.HarnessId));
            }
        }

        return map;
    }

    private static List<string> ReadWorkspaceComposerIds(IHarnessSqliteSource sqlite, string databasePath, HarnessSqliteQuery query)
    {
        using IHarnessSqliteDatabase database = sqlite.Open(databasePath);
        string? value = null;
        foreach (IReadOnlyDictionary<string, object?> row in database.Query(query))
        {
            value = row["value"] as string;
            break;
        }

        if (value is null)
        {
            throw new InvalidDataException("the ItemTable has no composer.composerData text.");
        }

        using JsonDocument document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("allComposers", out JsonElement composers)
            || composers.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("composer.composerData has no allComposers array.");
        }

        List<string> ids = [];
        foreach (JsonElement composer in composers.EnumerateArray())
        {
            if (composer.ValueKind == JsonValueKind.Object
                && composer.TryGetProperty("composerId", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } composerId)
            {
                ids.Add(composerId);
            }
        }

        return ids;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The composer has no bubble rows; it no longer exists.</exception>
    /// <remarks>
    /// The bubble values are read with <c>json_extract</c> in SQL, so the reader never parses a whole bubble.
    /// The user text is cut to 500 characters in SQL, and <c>text_length</c> keeps the full length.
    /// </remarks>
    public void WriteExport(IHarnessSqliteDatabase database, string conversationId, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(writer);

        HarnessSqliteQuery query = new(
            BubblesQueryName,
            """
            SELECT key AS bubble_key,
                   json_extract(value, '$.tokenCount.inputTokens') AS input_tokens,
                   json_extract(value, '$.tokenCount.outputTokens') AS output_tokens,
                   json_extract(value, '$.modelInfo.modelName') AS model,
                   json_extract(value, '$.createdAt') AS created_at,
                   json_extract(value, '$.requestId') AS request_id,
                   substr(json_extract(value, '$.text'), 1, 500) AS user_text,
                   length(json_extract(value, '$.text')) AS text_length,
                   json_extract(value, '$.type') AS bubble_type,
                   json_extract(value, '$.contextWindow') AS context_window,
                   json_extract(value, '$.codeBlocks') AS code_blocks
            FROM cursorDiskKV
            WHERE key >= ? AND key < ?
            ORDER BY ROWID
            """,
            [BubblePrefix + conversationId + ":", BubblePrefix + conversationId + ";"]);

        // The first row is read before the array opens, so a composer that has vanished writes nothing.
        using IEnumerator<IReadOnlyDictionary<string, object?>> rows = database.Query(query).GetEnumerator();
        if (!rows.MoveNext())
        {
            throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"Cursor composer {conversationId} has no bubble rows; it no longer exists."));
        }

        writer.WriteStartArray();
        do
        {
            WriteBubble(writer, rows.Current);
        }
        while (rows.MoveNext());
        writer.WriteEndArray();
    }

    private static void WriteBubble(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> row)
    {
        writer.WriteStartObject();
        WriteText(writer, "bubble_key", row["bubble_key"]);
        SqliteHarnessSourceReader.WriteJsonColumn(writer, "input_tokens", row["input_tokens"]);
        SqliteHarnessSourceReader.WriteJsonColumn(writer, "output_tokens", row["output_tokens"]);
        WriteText(writer, "model", row["model"]);
        WriteText(writer, "created_at", row["created_at"]);
        WriteText(writer, "request_id", row["request_id"]);
        WriteText(writer, "user_text", row["user_text"]);
        SqliteHarnessSourceReader.WriteJsonColumn(writer, "text_length", row["text_length"]);
        SqliteHarnessSourceReader.WriteJsonColumn(writer, "bubble_type", row["bubble_type"]);
        SqliteHarnessSourceReader.WriteJsonColumn(writer, "context_window", row["context_window"]);
        // code_blocks is a JSON array as text, so it is written as JSON, not as a string.
        SqliteHarnessSourceReader.WriteJsonColumn(writer, "code_blocks", row["code_blocks"]);
        writer.WriteEndObject();
    }

    // Text goes out as a JSON string even when it looks like JSON: user_text is prose, and the generic
    // writer would otherwise turn a bubble whose text is "[1, 2]" into an array. Non-text falls back to the generic writer.
    private static void WriteText(Utf8JsonWriter writer, string propertyName, object? value)
    {
        if (value is string text)
        {
            writer.WriteString(propertyName, text);
            return;
        }

        SqliteHarnessSourceReader.WriteJsonColumn(writer, propertyName, value);
    }

    private static string ReadText(IReadOnlyDictionary<string, object?> row, string column) =>
        row[column] as string
        ?? throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Cursor composer query returned no text in '{column}'."));

    private static long ReadInteger(IReadOnlyDictionary<string, object?> row, string column) =>
        row[column] switch
        {
            long value => value,
            int value => value,
            _ => throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Cursor composer query returned no integer in '{column}'.")),
        };
}
