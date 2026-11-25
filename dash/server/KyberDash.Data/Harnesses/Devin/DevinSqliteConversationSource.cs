using System.Globalization;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;

namespace KyberDash.Data.Harnesses.Devin;

/// <summary>
/// Reads the Devin CLI <c>sessions.db</c> for <see cref="SqliteHarnessSourceReader"/>: one unit per session
/// that holds at least one message node (hidden sessions included), exported as the D10 Devin document that
/// <see cref="DevinMapper"/> maps.
/// </summary>
/// <remarks>
/// A session with no nodes has nothing to map, so it is not listed; the inner join keeps it out of the
/// unit list rather than an in-memory filter. Hidden sessions are kept because the owner's hidden flag marks a
/// session the CLI does not show, not one whose conversation is absent. <c>last_activity_at</c> is stored as
/// ISO-8601 text and is read as UTC; text that does not parse refuses the database naming the session, because
/// a silent default would move the session's time window.
/// </remarks>
public sealed class DevinSqliteConversationSource : ISqliteConversationSource
{
    /// <summary>Lists each session with its node count; the revision token and the time window come from these.</summary>
    internal const string ConversationsQuery = "devin.conversations";

    /// <summary>Reads one session row for the export.</summary>
    internal const string SessionQuery = "devin.session";

    /// <summary>Reads one session's nodes in <c>row_id</c> order for the export.</summary>
    internal const string NodesQuery = "devin.message_nodes";

    private const string ConversationsSql =
        "SELECT s.id AS id, s.last_activity_at AS last_activity_at, COUNT(m.row_id) AS node_count " +
        "FROM sessions s JOIN message_nodes m ON m.session_id = s.id " +
        "GROUP BY s.id, s.last_activity_at ORDER BY s.id";

    private const string SessionSql =
        "SELECT id, model, title, working_directory, created_at, last_activity_at, metadata FROM sessions WHERE id = ?";

    private const string NodesSql =
        "SELECT node_id, parent_node_id, created_at, chat_message FROM message_nodes WHERE session_id = ? ORDER BY row_id";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredColumns { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["sessions"] = ["id", "working_directory", "model", "title", "created_at", "last_activity_at", "metadata"],
            ["message_nodes"] = ["row_id", "session_id", "node_id", "parent_node_id", "chat_message", "created_at"],
        };

    /// <inheritdoc />
    public IEnumerable<SqliteConversation> ListConversations(SqliteConversationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (IReadOnlyDictionary<string, object?> row in context.Database.Query(
                     new HarnessSqliteQuery(ConversationsQuery, ConversationsSql, [])))
        {
            string id = Text(row["id"]) ?? throw new InvalidDataException("Devin session with no id.");
            string activityText = Text(row["last_activity_at"]) ?? string.Empty;
            DateTimeOffset? lastActivity = ParseActivity(id, activityText);
            if (context.SinceUtc is { } since && lastActivity is { } activity && activity < since)
            {
                continue;
            }

            long nodeCount = Convert.ToInt64(row["node_count"], CultureInfo.InvariantCulture);
            yield return new SqliteConversation(id, $"{activityText}-{nodeCount}", lastActivity, CompanionKey: null);
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The session no longer exists.</exception>
    public void WriteExport(IHarnessSqliteDatabase database, string conversationId, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(writer);

        IReadOnlyDictionary<string, object?> session = database
            .Query(new HarnessSqliteQuery(SessionQuery, SessionSql, [conversationId]))
            .FirstOrDefault()
            ?? throw new InvalidDataException($"Devin session '{conversationId}' no longer exists.");

        writer.WriteStartObject();
        writer.WriteString("session_id", conversationId);
        WriteText(writer, "model", session["model"]);
        WriteText(writer, "title", session["title"]);
        WriteText(writer, "working_directory", session["working_directory"]);
        WriteText(writer, "created_at", session["created_at"]);
        WriteText(writer, "last_activity_at", session["last_activity_at"]);
        SqliteHarnessSourceReader.WriteJsonColumn(writer, "session_metadata", session["metadata"]);

        writer.WriteStartArray("message_nodes");
        foreach (IReadOnlyDictionary<string, object?> node in database.Query(
                     new HarnessSqliteQuery(NodesQuery, NodesSql, [conversationId])))
        {
            writer.WriteStartObject();
            WriteText(writer, "node_id", node["node_id"]);
            WriteText(writer, "parent_node_id", node["parent_node_id"]);
            WriteText(writer, "created_at", node["created_at"]);
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "chat_message", node["chat_message"]);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static DateTimeOffset? ParseActivity(string sessionId, string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            throw new InvalidDataException(
                $"Devin session '{sessionId}' has a last_activity_at that is not a date: '{text}'.");
        }

        return parsed;
    }

    /// <summary>Writes a text column as a JSON string, or null when the column is NULL. Never parses it as JSON.</summary>
    private static void WriteText(Utf8JsonWriter writer, string propertyName, object? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }

    private static string? Text(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
}
