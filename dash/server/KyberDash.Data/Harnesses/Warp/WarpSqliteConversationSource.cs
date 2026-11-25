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
/// </remarks>
public class WarpSqliteConversationSource : ISqliteConversationSource
{
    private const string ConversationsQueryName = "warp.conversations";
    private const string ConversationQueryName = "warp.conversation";
    private const string QueriesQueryName = "warp.queries";
    private const string BlocksQueryName = "warp.blocks";

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
        foreach (IReadOnlyDictionary<string, object?> block in database.Query(new HarnessSqliteQuery(
            BlocksQueryName,
            "SELECT block_id, start_ts, stylized_command FROM blocks "
                + "WHERE ai_metadata IS NOT NULL AND ai_metadata <> '' "
                + "AND json_extract(ai_metadata, '$.conversation_id') = ? ORDER BY start_ts ASC",
            [conversationId])))
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

    private static void WriteText(Utf8JsonWriter writer, string propertyName, IReadOnlyDictionary<string, object?> row, string column)
    {
        object? value = row.GetValueOrDefault(column);
        switch (value)
        {
            case null:
                writer.WriteNull(propertyName);
                break;
            case string text:
                writer.WriteString(propertyName, text);
                break;
            case byte[] bytes:
                writer.WriteString(propertyName, Convert.ToBase64String(bytes));
                break;
            default:
                writer.WriteString(propertyName, Convert.ToString(value, CultureInfo.InvariantCulture));
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
