using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Warp;
using KyberDash.Data.Tests.Collector;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Proves <see cref="WarpSqliteConversationSource"/> against a real <c>warp.sqlite</c> file opened by the
/// Microsoft.Data.Sqlite adapter: which conversations are listed, the SQL filters and order of the export,
/// and a round trip through <see cref="WarpMapper.Map"/> that yields one record per exchange.
/// </summary>
public class WarpSqliteConversationSourceSqliteTests : IDisposable
{
    private const string SchemaDdl = """
        CREATE TABLE agent_conversations (conversation_id TEXT PRIMARY KEY, conversation_data TEXT, last_modified_at TEXT);
        CREATE TABLE ai_queries (exchange_id TEXT PRIMARY KEY, conversation_id TEXT, start_ts TEXT, input TEXT,
            working_directory TEXT, output_status TEXT, model_id TEXT, planning_model_id TEXT, coding_model_id TEXT);
        CREATE TABLE blocks (block_id TEXT PRIMARY KEY, start_ts TEXT, stylized_command TEXT, ai_metadata TEXT);
        """;

    private const string ConversationData = "{\"conversation_usage_metadata\":{}}";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ListConversations_ListsOnlyConversationsWithAtLeastOneQuery()
    {
        string path = CreateWarpDatabase();
        WarpSqliteConversationSource source = new();
        using IHarnessSqliteDatabase database = Source().Open(path);

        List<SqliteConversation> listed = [.. source.ListConversations(Context(database, path, null))];

        SqliteConversation conversation = Assert.Single(listed);
        Assert.Equal("conv-1", conversation.Id);
        Assert.Equal("2026-05-18 10:10:00", conversation.RevisionToken);
        Assert.Equal(new DateTimeOffset(2026, 5, 18, 10, 10, 0, TimeSpan.Zero), conversation.LastActivityUtc);
    }

    [Fact]
    public void WriteExport_QueriesInStartOrder_AndBlocksOnlyForThisConversation()
    {
        string path = CreateWarpDatabase();
        WarpSqliteConversationSource source = new();
        using IHarnessSqliteDatabase database = Source().Open(path);

        JsonElement export = Export(source, database, "conv-1");

        Assert.Equal(["ex-a", "ex-b"], export.GetProperty("queries").EnumerateArray().Select(query => query.GetProperty("exchange_id").GetString()));
        Assert.Equal(["blk-1"], export.GetProperty("blocks").EnumerateArray().Select(block => block.GetProperty("block_id").GetString()));
    }

    [Fact]
    public void WriteExport_RoundTripThroughMapper_YieldsOneRecordPerExchange()
    {
        string path = CreateWarpDatabase();
        WarpSqliteConversationSource source = new();
        using IHarnessSqliteDatabase database = Source().Open(path);
        string rawText = ExportText(source, database, "conv-1");
        DateTimeOffset baseline = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
        NativeSourceUnit unit = new(
            new SourceUnit("warp", path + "#conv-1", "warp", "warp-reader", "1", "warp-test", "warp", "1-1", baseline, 0),
            rawText,
            rawText.Length,
            baseline);

        CollectedBatch batch = new WarpMapper().Map(unit, baseline);

        Assert.Empty(batch.Problems);
        Assert.Equal(2, batch.Records.Count(static record => record.Op == RecordOp.LlmInvoke));
    }

    [Fact]
    public void WriteExport_TwoDatabasesWithTheSameConversationId_EachReturnsItsOwnBlocks()
    {
        string first = CreateWarpDatabase();
        string second = CreateWarpDatabase("second.sqlite", "blk-other");
        WarpSqliteConversationSource source = new();
        using IHarnessSqliteDatabase firstDatabase = Source().Open(first);
        using IHarnessSqliteDatabase secondDatabase = Source().Open(second);

        _ = source.ListConversations(Context(firstDatabase, first, null)).ToList();
        JsonElement fromFirst = Export(source, firstDatabase, "conv-1");
        _ = source.ListConversations(Context(secondDatabase, second, null)).ToList();
        JsonElement fromSecond = Export(source, secondDatabase, "conv-1");

        Assert.Equal(["blk-1"], fromFirst.GetProperty("blocks").EnumerateArray().Select(block => block.GetProperty("block_id").GetString()));
        Assert.Equal(["blk-other"], fromSecond.GetProperty("blocks").EnumerateArray().Select(block => block.GetProperty("block_id").GetString()));
    }

    [Fact]
    public void ListConversations_PassesOverDifferentCopies_KeepAtMostOneCachedDatabase()
    {
        string first = CreateWarpDatabase();
        string second = CreateWarpDatabase("second.sqlite", "blk-other");
        WarpSqliteConversationSource source = new();
        using IHarnessSqliteDatabase firstDatabase = Source().Open(first);
        using IHarnessSqliteDatabase secondDatabase = Source().Open(second);

        _ = source.ListConversations(Context(firstDatabase, first, null)).ToList();
        _ = Export(source, firstDatabase, "conv-1");
        _ = source.ListConversations(Context(secondDatabase, second, null)).ToList();
        _ = Export(source, secondDatabase, "conv-1");

        Assert.Equal(1, source.CachedDatabaseCount);
    }

    private static JsonElement Export(WarpSqliteConversationSource source, IHarnessSqliteDatabase database, string conversationId) =>
        JsonDocument.Parse(ExportText(source, database, conversationId)).RootElement.Clone();

    private static string ExportText(WarpSqliteConversationSource source, IHarnessSqliteDatabase database, string conversationId)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            source.WriteExport(database, conversationId, writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private SqliteConversationContext Context(IHarnessSqliteDatabase database, string path, DateTimeOffset? since) =>
        new(
            database,
            path,
            "warp",
            since,
            "/home/u",
            new PhysicalHarnessFileSystem(),
            Source(),
            new List<CollectedProblem>());

    private MicrosoftDataSqliteHarnessSource Source() =>
        new(new HarnessSqliteLimits(), directory.Combine("cache"));

    private string CreateWarpDatabase(string fileName = "warp.sqlite", string blockId = "blk-1")
    {
        string path = directory.Combine(fileName);
        SqliteFixtureDatabase.Create(
            path,
            SchemaDdl,
            [
                // conv-1 has two queries; conv-2 has none and must not be listed.
                new SqliteFixtureStatement(
                    "INSERT INTO agent_conversations VALUES (?1, ?2, ?3)",
                    ["conv-1", ConversationData, "2026-05-18 10:10:00"]),
                new SqliteFixtureStatement(
                    "INSERT INTO agent_conversations VALUES (?1, ?2, ?3)",
                    ["conv-2", ConversationData, "2026-05-18 11:00:00"]),
                Query("ex-b", "conv-1", "2026-05-18 10:01:00"),
                Query("ex-a", "conv-1", "2026-05-18 10:00:00"),
                // blk-1 is attributed to conv-1; the others are for conv-2, unattributed, or empty metadata.
                new SqliteFixtureStatement(
                    "INSERT INTO blocks VALUES (?1, ?2, ?3, ?4)",
                    [blockId, "2026-05-18 10:00:30", "ls", "{\"conversation_id\":\"conv-1\"}"]),
                new SqliteFixtureStatement(
                    "INSERT INTO blocks VALUES (?1, ?2, ?3, ?4)",
                    ["blk-2", "2026-05-18 10:00:40", "pwd", "{\"conversation_id\":\"conv-2\"}"]),
                new SqliteFixtureStatement(
                    "INSERT INTO blocks VALUES (?1, ?2, ?3, ?4)",
                    ["blk-3", "2026-05-18 10:00:50", "date", null]),
                new SqliteFixtureStatement(
                    "INSERT INTO blocks VALUES (?1, ?2, ?3, ?4)",
                    ["blk-4", "2026-05-18 10:00:55", "whoami", ""]),
            ]);
        return path;
    }

    private static SqliteFixtureStatement Query(string exchangeId, string conversationId, string startTs) =>
        new(
            "INSERT INTO ai_queries VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
            [exchangeId, conversationId, startTs, "[]", "/home/u/project", "\"Completed\"", "claude-sonnet-4-5", "", ""]);
}
