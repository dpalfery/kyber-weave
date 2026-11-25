using System.IO;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Warp;
using KyberDash.Data.Tests.Collector;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="WarpSqliteConversationSource"/> against the in-memory database fake: which
/// conversations are listed, their revision and activity, and the export shape the mapper reads.
/// The SQL filters (conversations with queries, block attribution, order) are proved in
/// <c>WarpSqliteConversationSourceSqliteTests</c> against a real file.
/// </summary>
public class WarpSqliteConversationSourceTests
{
    private const string DbPath = "/home/u/Library/warp.sqlite";
    private const string ConversationData = "{\"conversation_usage_metadata\":{}}";

    [Fact]
    public void ListConversations_RevisionAndActivityComeFromLastModifiedAt()
    {
        Rig rig = new();
        rig.Database.AddRow("warp.conversations", Row(("conversation_id", "conv-1"), ("last_modified_at", "2026-05-18 10:10:00")));

        SqliteConversation conversation = Assert.Single(rig.List());

        Assert.Equal("conv-1", conversation.Id);
        Assert.Equal("2026-05-18 10:10:00", conversation.RevisionToken);
        Assert.Equal(new DateTimeOffset(2026, 5, 18, 10, 10, 0, TimeSpan.Zero), conversation.LastActivityUtc);
        Assert.Null(conversation.CompanionKey);
    }

    [Fact]
    public void ListConversations_SkipsConversationsBeforeSinceUtc()
    {
        Rig rig = new(since: new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero));
        rig.Database
            .AddRow("warp.conversations", Row(("conversation_id", "old"), ("last_modified_at", "2026-05-17 09:00:00")))
            .AddRow("warp.conversations", Row(("conversation_id", "new"), ("last_modified_at", "2026-05-18 10:30:00")));

        List<SqliteConversation> listed = rig.List();

        Assert.Equal(["new"], listed.Select(conversation => conversation.Id));
    }

    [Fact]
    public void ListConversations_UnreadableTimestamp_IsListedWithoutActivity()
    {
        Rig rig = new(since: new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero));
        rig.Database.AddRow("warp.conversations", Row(("conversation_id", "conv-1"), ("last_modified_at", null)));

        SqliteConversation conversation = Assert.Single(rig.List());

        Assert.Null(conversation.LastActivityUtc);
        Assert.Equal(string.Empty, conversation.RevisionToken);
    }

    [Fact]
    public void ListConversations_RowWithoutId_RecordsAProblemAndListsNothing()
    {
        Rig rig = new();
        rig.Database.AddRow("warp.conversations", Row(("conversation_id", null), ("last_modified_at", "2026-05-18 10:10:00")));

        Assert.Empty(rig.List());
        CollectedProblem problem = Assert.Single(rig.Problems);
        Assert.Equal(WarpMapper.MalformedDocumentCode, problem.Code);
        Assert.Equal(DbPath, problem.Location);
    }

    [Fact]
    public void WriteExport_WritesConversationQueriesAndBlocksAsStrings()
    {
        Rig rig = new();
        rig.Database
            .AddRow("warp.conversation", Row(
                ("conversation_id", "conv-1"),
                ("conversation_data", ConversationData),
                ("last_modified_at", "2026-05-18 10:10:00")))
            .AddRow("warp.queries", QueryRow("ex-1", "2026-05-18 10:00:00"))
            .AddRow("warp.queries", QueryRow("ex-2", "2026-05-18 10:01:00"))
            .AddRow("warp.blocks", Row(
                ("block_id", "blk-1"),
                ("start_ts", "2026-05-18 10:00:30"),
                ("stylized_command", "ls")));

        JsonElement export = rig.Export("conv-1");

        JsonElement conversation = export.GetProperty("conversation");
        Assert.Equal("conv-1", conversation.GetProperty("conversation_id").GetString());
        Assert.Equal(ConversationData, conversation.GetProperty("conversation_data").GetString());
        Assert.Equal("2026-05-18 10:10:00", conversation.GetProperty("last_modified_at").GetString());

        JsonElement[] queries = export.GetProperty("queries").EnumerateArray().ToArray();
        Assert.Equal(2, queries.Length);
        Assert.Equal(
            ["exchange_id", "conversation_id", "start_ts", "input", "working_directory", "output_status", "model_id", "planning_model_id", "coding_model_id"],
            queries[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal("ex-1", queries[0].GetProperty("exchange_id").GetString());
        Assert.Equal("claude-sonnet-4-5", queries[0].GetProperty("model_id").GetString());

        JsonElement block = Assert.Single(export.GetProperty("blocks").EnumerateArray().ToArray());
        Assert.Equal("blk-1", block.GetProperty("block_id").GetString());
        Assert.Equal("ls", block.GetProperty("stylized_command").GetString());
    }

    [Fact]
    public void WriteExport_BindsTheConversationIdToEveryQuery()
    {
        Rig rig = new();
        rig.Database.AddRow("warp.conversation", Row(("conversation_id", "conv-1")));

        rig.Export("conv-1");

        IReadOnlyList<HarnessSqliteQuery> executed = rig.Sqlite.ExecutedQueries(DbPath);
        Assert.Equal(3, executed.Count);
        Assert.All(executed, query => Assert.Equal("conv-1", Assert.Single(query.Parameters)));
    }

    [Fact]
    public void WriteExport_ConversationNoLongerPresent_Throws()
    {
        Rig rig = new();

        Assert.Throws<InvalidDataException>(() => rig.Export("gone"));
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Column, object? Value)[] values)
    {
        Dictionary<string, object?> row = new(StringComparer.Ordinal);
        foreach ((string column, object? value) in values)
        {
            row[column] = value;
        }

        return row;
    }

    private static IReadOnlyDictionary<string, object?> QueryRow(string exchangeId, string startTs) =>
        Row(
            ("exchange_id", exchangeId),
            ("conversation_id", "conv-1"),
            ("start_ts", startTs),
            ("input", "[]"),
            ("working_directory", "/home/u/project"),
            ("output_status", "\"Completed\""),
            ("model_id", "claude-sonnet-4-5"),
            ("planning_model_id", null),
            ("coding_model_id", ""));

    private sealed class Rig
    {
        private readonly DateTimeOffset? since;

        public Rig(DateTimeOffset? since = null)
        {
            this.since = since;
            Database = Sqlite.AddDatabase(DbPath);
        }

        public InMemoryHarnessSqliteSource Sqlite { get; } = new();

        public FakeDatabase Database { get; }

        public WarpSqliteConversationSource Source { get; } = new();

        public List<CollectedProblem> Problems { get; } = [];

        public List<SqliteConversation> List()
        {
            using IHarnessSqliteDatabase database = Sqlite.Open(DbPath);
            return [.. Source.ListConversations(Context(database))];
        }

        public JsonElement Export(string conversationId)
        {
            using IHarnessSqliteDatabase database = Sqlite.Open(DbPath);
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                Source.WriteExport(database, conversationId, writer);
            }

            using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            return document.RootElement.Clone();
        }

        private SqliteConversationContext Context(IHarnessSqliteDatabase database) =>
            new(
                database,
                DbPath,
                "warp",
                since,
                "/home/u",
                new PhysicalHarnessFileSystem(),
                Sqlite,
                Problems);
    }
}
