using System.Globalization;
using System.IO;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;

namespace KyberDash.Data.Harnesses.Warp;

/// <summary>
/// Conversation source for Warp's <c>warp.sqlite</c>: one unit per conversation that has at least one
/// <c>ai_queries</c> row, exported as the envelope <see cref="WarpMapper.Parse"/> reads.
/// </summary>
/// <remarks>
/// The schema is taken from the deleted <c>warp.ts</c> reader, not from a live database, so the
/// column names here are the contract. Every exported value is written as a JSON string, including
/// <c>conversation_data</c>: the mapper reads it with <c>GetString</c>, and writing it as an object
/// would make the usage data vanish without an error.
/// <para>
/// Block attribution is a JSON filter on <c>blocks.ai_metadata</c> that no index supports, and the
/// database is harness-owned so none can be added. Running it once per conversation would parse every
/// block once per conversation, so the attribution is read in one scan per database and kept by the file
/// SQLite reports for it. <see cref="ListConversations"/> empties the whole cache, so each discovery pass
/// reads the current blocks, the exports that follow it share that single scan, and the keys of earlier
/// fingerprinted copies (a new file per change) cannot accumulate for the life of the singleton.
/// </para>
/// </remarks>
public class WarpSqliteConversationSource : ISqliteConversationSource
{
    private const string ConversationsQueryName = "warp.conversations";
    private const string ConversationQueryName = "warp.conversation";
    private const string QueriesQueryName = "warp.queries";
    private const string BlocksQueryName = "warp.blocks";
    private const string BlockRowsQueryName = "warp.block-rows";

    /// <summary>Most block ids one export query carries.</summary>
    private const int BlockIdPageSize = 500;
    private const string DatabaseFileQueryName = "warp.database-file";
    private const string UnattributedBlocksQueryName = "warp.unattributed-blocks";

    private static readonly string[] QueryColumns =
    [
        "exchange_id",
        "conversation_id",
        "start_ts",
        "input",
        "working_directory",
        "output_status",
        "model_id",
        "planning_model_id",
        "coding_model_id",
    ];

    private static readonly string[] BlockColumns = ["block_id", "start_ts", "stylized_command"];

    private readonly object blocksLock = new();

    private readonly Dictionary<string, Dictionary<string, List<string>>> blocksByDatabase =
        new(StringComparer.Ordinal);

    /// <summary>Number of databases whose block attribution is currently kept; read by tests to prove the cache is bounded.</summary>
    internal int CachedDatabaseCount
    {
        get
        {
            lock (blocksLock)
            {
                return blocksByDatabase.Count;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredColumns { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["agent_conversations"] = ["conversation_id", "conversation_data", "last_modified_at"],
            ["ai_queries"] = QueryColumns,
            ["blocks"] = ["block_id", "start_ts", "stylized_command", "ai_metadata"],
        };

    /// <inheritdoc />
    /// <remarks>
    /// Lists conversations with a query row only. A conversation whose <c>last_modified_at</c> is before
    /// <see cref="SqliteConversationContext.SinceUtc"/> is skipped; one whose timestamp cannot be read is
    /// kept, with no activity time, so that it is not lost silently.
    /// </remarks>
    public IEnumerable<SqliteConversation> ListConversations(SqliteConversationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (blocksLock)
        {
            blocksByDatabase.Clear();
        }

        RecordUnattributableBlocks(context);

        HarnessSqliteQuery query = new(
            ConversationsQueryName,
            "SELECT conversation_id, last_modified_at FROM agent_conversations AS c "
                + "WHERE EXISTS (SELECT 1 FROM ai_queries AS q WHERE q.conversation_id = c.conversation_id) "
                + "ORDER BY conversation_id ASC",
            []);

        foreach (IReadOnlyDictionary<string, object?> row in context.Database.Query(query))
        {
            string? id = ReadText(row, "conversation_id");
            if (string.IsNullOrEmpty(id))
            {
                context.Problems.Add(new CollectedProblem(
                    ProblemKey: string.Create(CultureInfo.InvariantCulture, $"warp-malformed:{context.DatabasePath}:no-conversation-id"),
                    Severity: "warning",
                    Code: WarpMapper.MalformedDocumentCode,
                    Message: "Skipping Warp conversation row without a conversation_id.",
                    SpanId: null,
                    Location: context.DatabasePath,
                    SessionKey: null,
                    HarnessId: context.HarnessId,
                    TimestampUtc: null,
                    Stack: null));
                continue;
            }

            string? lastModified = ReadText(row, "last_modified_at");
            DateTimeOffset? activity = ParseTimestamp(lastModified);
            if (context.SinceUtc is { } since && activity is { } at && at < since)
            {
                continue;
            }

            yield return new SqliteConversation(id, lastModified ?? string.Empty, activity, null);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The conversation no longer exists.</exception>
    public void WriteExport(IHarnessSqliteDatabase database, string conversationId, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(writer);

        IReadOnlyDictionary<string, object?>? conversation = database
            .Query(new HarnessSqliteQuery(
                ConversationQueryName,
                "SELECT conversation_id, conversation_data, last_modified_at FROM agent_conversations WHERE conversation_id = ?",
                [conversationId]))
            .FirstOrDefault();
        if (conversation is null)
        {
            throw new InvalidDataException($"Warp conversation '{conversationId}' no longer exists.");
        }

        writer.WriteStartObject();

        writer.WriteStartObject("conversation");
        WriteText(writer, "conversation_id", conversation, "conversation_id");
        WriteText(writer, "conversation_data", conversation, "conversation_data");
        WriteText(writer, "last_modified_at", conversation, "last_modified_at");
        writer.WriteEndObject();

        writer.WriteStartArray("queries");
        foreach (IReadOnlyDictionary<string, object?> query in database.Query(new HarnessSqliteQuery(
            QueriesQueryName,
            "SELECT exchange_id, conversation_id, start_ts, input, working_directory, output_status, "
                + "model_id, planning_model_id, coding_model_id FROM ai_queries WHERE conversation_id = ? "
                + "ORDER BY start_ts ASC",
            [conversationId])))
        {
            writer.WriteStartObject();
            foreach (string column in QueryColumns)
            {
                WriteText(writer, column, query, column);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("blocks");
        foreach (IReadOnlyDictionary<string, object?> block in BlocksFor(database, conversationId))
        {
            writer.WriteStartObject();
            foreach (string column in BlockColumns)
            {
                WriteText(writer, column, block, column);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// The export attributes a block by a text <c>conversation_id</c>; a block carrying any other JSON type
    /// there cannot be attributed and is left out, so the count of them is recorded rather than dropped silently.
    /// </summary>
    private static void RecordUnattributableBlocks(SqliteConversationContext context)
    {
        IReadOnlyDictionary<string, object?>? row = context.Database
            .Query(new HarnessSqliteQuery(
                UnattributedBlocksQueryName,
                "SELECT COUNT(*) AS skipped FROM blocks WHERE ai_metadata IS NOT NULL AND ai_metadata <> '' "
                    + "AND json_type(ai_metadata, '$.conversation_id') NOT IN ('text', 'null')",
                []))
            .FirstOrDefault();
        long skipped = row?.GetValueOrDefault("skipped") is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0L;
        if (skipped <= 0)
        {
            return;
        }

        context.Problems.Add(new CollectedProblem(
            ProblemKey: string.Create(CultureInfo.InvariantCulture, $"warp-malformed:{context.DatabasePath}:blocks-non-text-conversation-id"),
            Severity: "warning",
            Code: WarpMapper.MalformedDocumentCode,
            Message: string.Create(CultureInfo.InvariantCulture, $"Skipping {skipped} Warp block(s) whose conversation_id is not text."),
            SpanId: null,
            Location: context.DatabasePath,
            SessionKey: null,
            HarnessId: context.HarnessId,
            TimestampUtc: null,
            Stack: null));
    }

    /// <summary>
    /// The scan keeps only each conversation's block ids for the pass, not the block text, so memory does
    /// not grow with the size of the commands; an export then fetches just its own conversation's rows.
    /// The scan lists the ids in start order, so fetching them in pages of <see cref="BlockIdPageSize"/>,
    /// each page in the same order, keeps the whole export in start order. The rows stream straight into
    /// the writer page by page, so the export cap stops the read while pages are still coming, and no
    /// query carries more than one page of ids.
    /// </summary>
    private IEnumerable<IReadOnlyDictionary<string, object?>> BlocksFor(IHarnessSqliteDatabase database, string conversationId)
    {
        string path = DatabaseKey(database);
        List<string>? blockIds;
        lock (blocksLock)
        {
            if (!blocksByDatabase.TryGetValue(path, out Dictionary<string, List<string>>? byConversation))
            {
                byConversation = ReadBlockIdsByConversation(database);
                blocksByDatabase[path] = byConversation;
            }

            byConversation.TryGetValue(conversationId, out blockIds);
        }

        if (blockIds is null or { Count: 0 })
        {
            return [];
        }

        return BlockPages(database, blockIds);
    }

    private static IEnumerable<IReadOnlyDictionary<string, object?>> BlockPages(IHarnessSqliteDatabase database, List<string> blockIds)
    {
        for (int start = 0; start < blockIds.Count; start += BlockIdPageSize)
        {
            string idArray = JsonSerializer.Serialize(blockIds.GetRange(start, Math.Min(BlockIdPageSize, blockIds.Count - start)));
            foreach (IReadOnlyDictionary<string, object?> row in database.Query(new HarnessSqliteQuery(
                BlockRowsQueryName,
                "SELECT block_id, start_ts, stylized_command FROM blocks "
                    + "WHERE block_id IN (SELECT value FROM json_each(?)) ORDER BY start_ts ASC, block_id ASC",
                [idArray])))
            {
                yield return row;
            }
        }
    }

    /// <summary>
    /// Identifies an opened database by the file SQLite reports for it. The seam hands an export no path,
    /// and the adapter may have opened a fingerprinted copy, which this key then names, so a changed
    /// database never shares an entry with an older one. A handle that reports no file (the in-memory
    /// fake) shares one empty key.
    /// </summary>
    private static string DatabaseKey(IHarnessSqliteDatabase database) =>
        database
            .Query(new HarnessSqliteQuery(
                DatabaseFileQueryName,
                "SELECT file FROM pragma_database_list WHERE name = 'main'",
                []))
            .Select(static row => row.GetValueOrDefault("file") as string)
            .FirstOrDefault(static file => !string.IsNullOrEmpty(file))
        ?? string.Empty;

    private static Dictionary<string, List<string>> ReadBlockIdsByConversation(IHarnessSqliteDatabase database)
    {
        Dictionary<string, List<string>> byConversation = new(StringComparer.Ordinal);
        foreach (IReadOnlyDictionary<string, object?> row in database.Query(new HarnessSqliteQuery(
            BlocksQueryName,
            "SELECT json_extract(ai_metadata, '$.conversation_id') AS conversation_id, block_id "
                + "FROM blocks WHERE ai_metadata IS NOT NULL AND ai_metadata <> '' ORDER BY start_ts ASC, block_id ASC",
            [])))
        {
            if (row.GetValueOrDefault("conversation_id") is not string owner
                || row.GetValueOrDefault("block_id") is not string blockId)
            {
                continue;
            }

            if (!byConversation.TryGetValue(owner, out List<string>? blockIds))
            {
                blockIds = [];
                byConversation[owner] = blockIds;
            }

            blockIds.Add(blockId);
        }

        return byConversation;
    }

    private static void WriteText(Utf8JsonWriter writer, string propertyName, IReadOnlyDictionary<string, object?> row, string column)
    {
        object? value = row.GetValueOrDefault(column);
        switch (value)
        {
            case null:
                writer.WriteNull(propertyName);
                break;
            case string text:
                SqliteHarnessSourceReader.WriteJsonString(writer, propertyName, text);
                break;
            case byte[] bytes:
                SqliteHarnessSourceReader.WriteJsonColumn(writer, propertyName, bytes);
                break;
            default:
                SqliteHarnessSourceReader.WriteJsonString(writer, propertyName, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static string? ReadText(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            string text => text,
            var other => Convert.ToString(other, CultureInfo.InvariantCulture),
        };

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string text = raw.Trim();
        if (!text.Contains('T', StringComparison.Ordinal))
        {
            text = text.Replace(' ', 'T');
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}
