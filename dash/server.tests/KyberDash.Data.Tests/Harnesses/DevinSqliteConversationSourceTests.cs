using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Devin;
using KyberDash.Data.Tests.Collector;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// In-memory tests for <see cref="DevinSqliteConversationSource"/>: the units it lists, their revision
/// tokens, the <c>SinceUtc</c> cutoff and the D10 export, against canned rows from the fake database.
/// </summary>
public class DevinSqliteConversationSourceTests
{
    private const string DatabasePath = "/home/u/.local/share/devin/cli/sessions.db";

    [Fact]
    public void ListConversations_ReturnsOneUnitPerRow_WithRevisionFromActivityAndNodeCount()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath)
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("s1", "2026-11-20T10:00:00Z", 3L));
        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);

        List<SqliteConversation> listed = [.. new DevinSqliteConversationSource().ListConversations(
            Context(database, since: null))];

        SqliteConversation conversation = Assert.Single(listed);
        Assert.Equal("s1", conversation.Id);
        Assert.Equal("2026-11-20T10:00:00Z-3", conversation.RevisionToken);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), conversation.LastActivityUtc);
        Assert.Null(conversation.CompanionKey);
    }

    [Fact]
    public void ListConversations_SkipsSessionsLastActiveBeforeSince_AndKeepsTheCutoffItself()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath)
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("old", "2026-10-01T00:00:00Z", 1L))
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("edge", "2026-11-01T00:00:00Z", 1L))
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("new", "2026-11-20T00:00:00Z", 1L));
        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);

        List<string> ids = [.. new DevinSqliteConversationSource().ListConversations(Context(
                database,
                since: new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)))
            .Select(conversation => conversation.Id)];

        Assert.Equal(["edge", "new"], ids);
    }

    [Fact]
    public void ListConversations_ActivityTextWithAnOffset_IsReadAsUtc()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath)
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("s1", "2026-11-20T12:00:00+02:00", 1L));
        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);

        SqliteConversation conversation = Assert.Single(new DevinSqliteConversationSource().ListConversations(
            Context(database, since: null)));

        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), conversation.LastActivityUtc);
    }

    [Fact]
    public void ListConversations_UnparseableActivity_RecordsAProblemNamingTheSession_AndListsTheOthers()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath)
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("s1", "yesterday-ish", 1L))
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("s2", "2026-11-20T10:00:00Z", 2L));
        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);
        List<CollectedProblem> problems = [];

        List<SqliteConversation> listed = [.. new DevinSqliteConversationSource().ListConversations(
            Context(database, since: null, problems))];

        Assert.Equal("s2", Assert.Single(listed).Id);
        CollectedProblem problem = Assert.Single(problems);
        Assert.Contains("s1", problem.Message, StringComparison.Ordinal);
        Assert.Equal(DatabasePath, problem.Location);
    }

    [Fact]
    public void ListConversations_NonIntegerNodeCount_RecordsAProblemAndListsTheOthers()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath)
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("s1", "2026-11-20T10:00:00Z", "many"))
            .AddRow(DevinSqliteConversationSource.ConversationsQuery, Row("s2", "2026-11-20T10:00:00Z", 2L));
        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);
        List<CollectedProblem> problems = [];

        List<SqliteConversation> listed = [.. new DevinSqliteConversationSource().ListConversations(
            Context(database, since: null, problems))];

        Assert.Equal("s2", Assert.Single(listed).Id);
        Assert.Contains("s1", Assert.Single(problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredColumns_NameTheColumnsEachQueryReads()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> required = new DevinSqliteConversationSource().RequiredColumns;

        Assert.Equal(
            ["id", "working_directory", "model", "title", "created_at", "last_activity_at", "metadata"],
            required["sessions"]);
        Assert.Equal(
            ["row_id", "session_id", "node_id", "parent_node_id", "chat_message", "created_at"],
            required["message_nodes"]);
    }

    [Fact]
    public void WriteExport_WritesTheD10Shape_WithNodesInTheOrderTheyArrive_AndPayloadsRaw()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath)
            .AddRow(DevinSqliteConversationSource.SessionQuery, new Dictionary<string, object?>
            {
                ["id"] = "s1",
                ["model"] = "gpt-x",
                ["title"] = "Fix the build",
                ["working_directory"] = "/repo",
                ["created_at"] = "2026-11-20T09:00:00Z",
                ["last_activity_at"] = "2026-11-20T10:00:00Z",
                ["metadata"] = "{\"acu\":1.5}",
            })
            .AddRow(DevinSqliteConversationSource.NodesQuery, new Dictionary<string, object?>
            {
                ["node_id"] = "n1",
                ["parent_node_id"] = null,
                ["created_at"] = "2026-11-20T09:00:01Z",
                ["chat_message"] = "{\"role\":\"user\",\"content\":\"hi\"}",
            })
            .AddRow(DevinSqliteConversationSource.NodesQuery, new Dictionary<string, object?>
            {
                ["node_id"] = "n2",
                ["parent_node_id"] = "n1",
                ["created_at"] = "2026-11-20T09:00:02Z",
                ["chat_message"] = "plain text reply",
            });
        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);

        JsonElement export = Export(database, "s1");

        Assert.Equal("s1", export.GetProperty("session_id").GetString());
        Assert.Equal("gpt-x", export.GetProperty("model").GetString());
        Assert.Equal("Fix the build", export.GetProperty("title").GetString());
        Assert.Equal("/repo", export.GetProperty("working_directory").GetString());
        Assert.Equal("2026-11-20T09:00:00Z", export.GetProperty("created_at").GetString());
        Assert.Equal("2026-11-20T10:00:00Z", export.GetProperty("last_activity_at").GetString());
        Assert.Equal(1.5, export.GetProperty("session_metadata").GetProperty("acu").GetDouble());

        JsonElement[] nodes = [.. export.GetProperty("message_nodes").EnumerateArray()];
        Assert.Equal(["n1", "n2"], nodes.Select(node => node.GetProperty("node_id").GetString()));
        Assert.Equal(JsonValueKind.Null, nodes[0].GetProperty("parent_node_id").ValueKind);
        Assert.Equal("n1", nodes[1].GetProperty("parent_node_id").GetString());
        Assert.Equal("user", nodes[0].GetProperty("chat_message").GetProperty("role").GetString());
        Assert.Equal("plain text reply", nodes[1].GetProperty("chat_message").GetString());
        Assert.Equal(
            ["session_id", "model", "title", "working_directory", "created_at", "last_activity_at", "session_metadata", "message_nodes"],
            export.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["node_id", "parent_node_id", "created_at", "chat_message"],
            nodes[0].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void WriteExport_NullSessionMetadata_WritesJsonNull()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath)
            .AddRow(DevinSqliteConversationSource.SessionQuery, Session("s1", metadata: null))
            .AddRow(DevinSqliteConversationSource.NodesQuery, Node("n1", "{\"role\":\"user\"}"));
        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);

        JsonElement export = Export(database, "s1");

        Assert.Equal(JsonValueKind.Null, export.GetProperty("session_metadata").ValueKind);
    }

    [Fact]
    public void WriteExport_SessionNoLongerThere_Refuses()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath);
        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);

        Assert.Throws<InvalidDataException>(() => Export(database, "gone"));
    }

    private static Dictionary<string, object?> Row(string id, string lastActivityAt, object nodeCount) => new()
    {
        ["id"] = id,
        ["last_activity_at"] = lastActivityAt,
        ["node_count"] = nodeCount,
    };

    private static Dictionary<string, object?> Session(string id, string? metadata) => new()
    {
        ["id"] = id,
        ["model"] = "gpt-x",
        ["title"] = "t",
        ["working_directory"] = "/repo",
        ["created_at"] = "2026-11-20T09:00:00Z",
        ["last_activity_at"] = "2026-11-20T10:00:00Z",
        ["metadata"] = metadata,
    };

    private static Dictionary<string, object?> Node(string nodeId, string chatMessage) => new()
    {
        ["node_id"] = nodeId,
        ["parent_node_id"] = null,
        ["created_at"] = "2026-11-20T09:00:01Z",
        ["chat_message"] = chatMessage,
    };

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

    private static SqliteConversationContext Context(IHarnessSqliteDatabase database, DateTimeOffset? since) =>
        Context(database, since, []);

    private static SqliteConversationContext Context(
        IHarnessSqliteDatabase database,
        DateTimeOffset? since,
        ICollection<CollectedProblem> problems) => new(
        database,
        DatabasePath,
        "devin",
        since,
        "/home/u",
        new NoFiles(),
        new InMemoryHarnessSqliteSource(),
        problems);

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

        public string GetFullPath(string path) => throw new NotSupportedException();
    }
}
