using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Devin;
using KyberDash.Data.Tests.Collector;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Proves <see cref="DevinSqliteConversationSource"/> against a real <c>sessions.db</c> in the F4 schema,
/// opened through the Microsoft.Data.Sqlite adapter: the node-count filter, hidden sessions, the
/// <c>SinceUtc</c> cutoff on <c>last_activity_at</c> and the export in <c>row_id</c> order.
/// </summary>
public sealed class DevinSqliteConversationSourceSqliteTests : IDisposable
{
    private const string Ddl =
        "CREATE TABLE sessions (id TEXT PRIMARY KEY, working_directory TEXT, model TEXT, title TEXT, " +
        "created_at TEXT, last_activity_at TEXT, hidden INTEGER, metadata TEXT); " +
        "CREATE TABLE message_nodes (row_id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, node_id TEXT NOT NULL, " +
        "parent_node_id TEXT, chat_message TEXT NOT NULL, created_at TEXT, metadata TEXT, UNIQUE(session_id, node_id));";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the scratch directory when disposing.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void ListConversations_ListsSessionsWithNodes_IncludingHidden_AndNotEmptyOnes()
    {
        string path = CreateSessions(directory.Path);
        using IHarnessSqliteDatabase database = Source().Open(path);

        List<SqliteConversation> listed = [.. new DevinSqliteConversationSource().ListConversations(Context(database, since: null))];

        Assert.Equal(["a", "hidden", "older"], listed.Select(conversation => conversation.Id));
        Assert.DoesNotContain("empty", listed.Select(conversation => conversation.Id));
    }

    [Fact]
    public void ListConversations_RevisionIsActivityTextDashNodeCount()
    {
        string path = CreateSessions(directory.Path);
        using IHarnessSqliteDatabase database = Source().Open(path);

        SqliteConversation conversation = new DevinSqliteConversationSource()
            .ListConversations(Context(database, since: null))
            .Single(listed => listed.Id == "a");

        Assert.Equal("2026-11-20T10:00:00Z-2", conversation.RevisionToken);
    }

    [Fact]
    public void ListConversations_SinceUtc_FiltersOnLastActivity()
    {
        string path = CreateSessions(directory.Path);
        using IHarnessSqliteDatabase database = Source().Open(path);

        List<string> ids = [.. new DevinSqliteConversationSource()
            .ListConversations(Context(database, since: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)))
            .Select(conversation => conversation.Id)];

        Assert.Equal(["a", "hidden"], ids);
    }

    [Fact]
    public void WriteExport_NodesInRowIdOrder_ChatMessageRaw_SessionMetadataRawOrNull()
    {
        string path = CreateSessions(directory.Path);
        using IHarnessSqliteDatabase database = Source().Open(path);

        JsonElement export = Export(database, "a");
        JsonElement hidden = Export(database, "hidden");

        Assert.Equal(1.5, export.GetProperty("session_metadata").GetProperty("acu").GetDouble());
        JsonElement[] nodes = [.. export.GetProperty("message_nodes").EnumerateArray()];
        Assert.Equal(["n1", "n2"], nodes.Select(node => node.GetProperty("node_id").GetString()));
        Assert.Equal("user", nodes[0].GetProperty("chat_message").GetProperty("role").GetString());
        Assert.Equal("plain reply", nodes[1].GetProperty("chat_message").GetString());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("session_metadata").ValueKind);
        Assert.Equal("hidden", hidden.GetProperty("session_id").GetString());
    }

    [Fact]
    public void WriteExport_UnknownSession_Refuses()
    {
        string path = CreateSessions(directory.Path);
        using IHarnessSqliteDatabase database = Source().Open(path);

        Assert.Throws<InvalidDataException>(() => Export(database, "gone"));
    }

    private MicrosoftDataSqliteHarnessSource Source() =>
        new(new HarnessSqliteLimits(), Path.Combine(directory.Path, "cache"));

    private static string CreateSessions(string parent)
    {
        string path = Path.Combine(parent, "sessions.db");
        SqliteFixtureDatabase.Create(path, Ddl,
        [
            Session("a", "2026-11-20T10:00:00Z", hidden: 0, "{\"acu\":1.5}"),
            Session("hidden", "2026-11-21T08:00:00Z", hidden: 1, null),
            Session("older", "2026-10-01T00:00:00Z", hidden: 0, null),
            Session("empty", "2026-11-22T00:00:00Z", hidden: 0, null),
            // Inserted out of row_id order: the export must still sort by row_id.
            Node(20, "a", "n2", "plain reply"),
            Node(10, "a", "n1", "{\"role\":\"user\",\"content\":\"hi\"}"),
            Node(30, "hidden", "h1", "{\"role\":\"user\"}"),
            Node(40, "older", "o1", "{\"role\":\"user\"}"),
        ]);
        return path;
    }

    private static SqliteFixtureStatement Session(string id, string lastActivityAt, int hidden, string? metadata) =>
        new(
            "INSERT INTO sessions (id, working_directory, model, title, created_at, last_activity_at, hidden, metadata) " +
            "VALUES (?1, '/repo', 'gpt-x', 'title', '2026-11-20T09:00:00Z', ?2, ?3, ?4);",
            [id, lastActivityAt, (long)hidden, metadata]);

    private static SqliteFixtureStatement Node(long rowId, string sessionId, string nodeId, string chatMessage) =>
        new(
            "INSERT INTO message_nodes (row_id, session_id, node_id, parent_node_id, chat_message, created_at, metadata) " +
            "VALUES (?1, ?2, ?3, NULL, ?4, '2026-11-20T09:00:01Z', NULL);",
            [rowId, sessionId, nodeId, chatMessage]);

    private static SqliteConversationContext Context(IHarnessSqliteDatabase database, DateTimeOffset? since) => new(
        database,
        "sessions.db",
        "devin",
        since,
        "/home/u",
        new NoFiles(),
        new InMemoryHarnessSqliteSource(),
        new List<CollectedProblem>());

    private static JsonElement Export(IHarnessSqliteDatabase database, string conversationId)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            new DevinSqliteConversationSource().WriteExport(database, conversationId, writer);
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>The source never reads sibling files, so every file call here is a test defect.</summary>
    private sealed class NoFiles : IHarnessFileSystem
    {
        public bool DirectoryExists(string path) => throw new NotSupportedException();

        public bool FileExists(string path) => throw new NotSupportedException();

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive) =>
            throw new NotSupportedException();

        public IEnumerable<string> EnumerateDirectories(string directoryPath) => throw new NotSupportedException();

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc) =>
            throw new NotSupportedException();

        public string ReadAllText(string filePath) => throw new NotSupportedException();

        public string ReadAllText(string filePath, long maxBytes)
        {
            string text = ReadAllText(filePath);
            return System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes
                ? text
                : throw new InvalidDataException($"'{filePath}' holds more than {maxBytes} bytes.");
        }

        public string GetFullPath(string path) => throw new NotSupportedException();
    }
}
