using System.Globalization;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;

namespace KyberDash.Data.Harnesses.Kilo;

/// <summary>
/// Lists and exports the Kilo shared-runtime conversations in <c>kilo.db</c> for the generic SQLite reader.
/// </summary>
/// <remarks>
/// A conversation is a root session (<c>parent_id IS NULL</c>, not archived). Its export holds the messages and
/// parts of the root and of every non-archived descendant, because Kilo runs subagent work in child sessions
/// (decision D13); 411 of 510 sessions on the owner's machine are children. Each row keeps its own
/// <c>session_id</c> so the mapper can attribute it. The tree walk mirrors the deleted
/// <c>sqlite-session-parser.ts</c> <c>session_tree</c> query.
/// </remarks>
public class KiloSqliteConversationSource : ISqliteConversationSource
{
    private const string RootsQuery = "kilo-roots";
    private const string RevisionQuery = "kilo-revision";
    private const string RootQuery = "kilo-root";
    private const string MessagesQuery = "kilo-messages";
    private const string PartsQuery = "kilo-parts";

    private const string SessionTree =
        "WITH RECURSIVE session_tree(id) AS ("
        + "SELECT id FROM session WHERE id = ? "
        + "UNION "
        + "SELECT child.id FROM session child JOIN session_tree parent ON child.parent_id = parent.id "
        + "WHERE child.time_archived IS NULL)";

    private const string RootsSql =
        "SELECT id, title FROM session WHERE parent_id IS NULL AND time_archived IS NULL ORDER BY id";

    private const string RevisionSql = SessionTree
        + " SELECT MAX(m.time_updated) AS max_updated, COUNT(m.id) AS message_count"
        + " FROM message m WHERE m.session_id IN (SELECT id FROM session_tree)";

    private const string RootSql = "SELECT id, title FROM session WHERE id = ?";

    private const string MessagesSql = SessionTree
        + " SELECT m.id, m.session_id, m.time_created, m.data"
        + " FROM message m WHERE m.session_id IN (SELECT id FROM session_tree)"
        + " ORDER BY m.time_created, m.id";

    private const string PartsSql = SessionTree
        + " SELECT p.id, p.message_id, p.session_id, p.time_created, p.data"
        + " FROM part p WHERE p.session_id IN (SELECT id FROM session_tree)"
        + " ORDER BY p.message_id, p.id";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredColumns { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["session"] = ["id", "parent_id", "title", "time_archived"],
            ["message"] = ["id", "session_id", "time_created", "time_updated", "data"],
            ["part"] = ["id", "message_id", "session_id", "time_created", "data"],
        };

    /// <summary>
    /// Lists the root sessions. The revision token is the tree's latest message update and message count, so it
    /// changes whenever a child session gains a message.
    /// </summary>
    /// <remarks>
    /// A root whose tree has no messages has no activity time, so it is never skipped by <c>SinceUtc</c>; it is
    /// listed with a zero revision component rather than dropped.
    /// </remarks>
    public IEnumerable<SqliteConversation> ListConversations(SqliteConversationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Materialised first: a nested query while the roots are still streaming would share the connection.
        List<IReadOnlyDictionary<string, object?>> roots =
            [.. context.Database.Query(new HarnessSqliteQuery(RootsQuery, RootsSql, []))];

        List<SqliteConversation> conversations = [];
        foreach (IReadOnlyDictionary<string, object?> root in roots)
        {
            string id = TextOf(root["id"]);
            IReadOnlyDictionary<string, object?>? stats = context.Database
                .Query(new HarnessSqliteQuery(RevisionQuery, RevisionSql, [id]))
                .FirstOrDefault();
            long? latestMs = stats is null ? null : OptionalLong(stats["max_updated"]);
            long messageCount = stats is null ? 0L : Convert.ToInt64(stats["message_count"], CultureInfo.InvariantCulture);
            DateTimeOffset? lastActivity = latestMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

            if (context.SinceUtc is { } since && lastActivity is { } latest && latest < since)
            {
                continue;
            }

            string revision = string.Create(CultureInfo.InvariantCulture, $"{latestMs ?? 0L}-{messageCount}");
            conversations.Add(new SqliteConversation(id, revision, lastActivity, CompanionKey: null));
        }

        return conversations;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every value is bound as a parameter; the SQL is the constant text above. Message and part data is written
    /// through <see cref="SqliteHarnessSourceReader.WriteJsonColumn"/>, so JSON is embedded raw and other text as a string.
    /// </remarks>
    public void WriteExport(IHarnessSqliteDatabase database, string conversationId, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(writer);

        IReadOnlyDictionary<string, object?>? root = database
            .Query(new HarnessSqliteQuery(RootQuery, RootSql, [conversationId]))
            .FirstOrDefault();
        if (root is null)
        {
            throw new InvalidDataException($"The Kilo session '{conversationId}' no longer exists.");
        }

        List<IReadOnlyDictionary<string, object?>> messages =
            [.. database.Query(new HarnessSqliteQuery(MessagesQuery, MessagesSql, [conversationId]))];
        List<IReadOnlyDictionary<string, object?>> parts =
            [.. database.Query(new HarnessSqliteQuery(PartsQuery, PartsSql, [conversationId]))];

        writer.WriteStartObject();
        writer.WriteString("session_id", TextOf(root["id"]));
        WriteText(writer, "title", root["title"]);

        writer.WriteStartArray("messages");
        foreach (IReadOnlyDictionary<string, object?> message in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("id", TextOf(message["id"]));
            writer.WriteString("session_id", TextOf(message["session_id"]));
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "time_created", message["time_created"]);
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "data", message["data"]);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("parts");
        foreach (IReadOnlyDictionary<string, object?> part in parts)
        {
            writer.WriteStartObject();
            writer.WriteString("id", TextOf(part["id"]));
            writer.WriteString("message_id", TextOf(part["message_id"]));
            writer.WriteString("session_id", TextOf(part["session_id"]));
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "time_created", part["time_created"]);
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "data", part["data"]);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteText(Utf8JsonWriter writer, string propertyName, object? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, TextOf(value));
        }
    }

    private static string TextOf(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static long? OptionalLong(object? value) =>
        value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
