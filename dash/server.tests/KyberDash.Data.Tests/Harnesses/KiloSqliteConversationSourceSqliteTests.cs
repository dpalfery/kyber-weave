using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Kilo;
using KyberDash.Data.Tests.Collector;
using KyberDash.Domain;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Proves <see cref="KiloSqliteConversationSource"/> against a real <c>kilo.db</c> in the F4 schema: root
/// selection, the subagent tree, archived rows, the revision token and the export round trip through
/// <see cref="KiloMapper"/>.
/// </summary>
public sealed class KiloSqliteConversationSourceSqliteTests : IDisposable
{
    private const string Ddl = """
        CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, directory TEXT, title TEXT,
            time_created INTEGER, time_updated INTEGER, time_archived INTEGER);
        CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
            time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL);
        CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL,
            time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL);
        """;

    private const string DatabaseFile = "kilo.db";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ListConversations_ListsOnlyUnarchivedRootSessions()
    {
        string path = CreateFixture();
        using IHarnessSqliteDatabase database = Source().Open(path);

        List<SqliteConversation> conversations = [.. ListWith(database, path, since: null)];

        Assert.Equal(["root-1", "root-2"], conversations.Select(static c => c.Id));
    }

    [Fact]
    public void ListConversations_RevisionUsesTreeMaxUpdateAndMessageCountAndIgnoresArchivedRows()
    {
        string path = CreateFixture();
        using IHarnessSqliteDatabase database = Source().Open(path);

        SqliteConversation root = ListWith(database, path, since: null).Single(static c => c.Id == "root-1");

        // Messages m1, m4 and m2 are in the tree (latest update 3000); m3 sits in the archived child and is excluded.
        Assert.Equal("3000-3", root.RevisionToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(3000), root.LastActivityUtc);
    }

    [Fact]
    public void ListConversations_SinceUtcSkipsATreeWhoseLatestUpdateIsOlder()
    {
        string path = CreateFixture();
        using IHarnessSqliteDatabase database = Source().Open(path);

        List<string> ids = [.. ListWith(database, path, DateTimeOffset.FromUnixTimeMilliseconds(3001)).Select(static c => c.Id)];

        // root-1's latest update is 3000; root-2 has no messages, so there is no activity to be older.
        Assert.Equal(["root-2"], ids);
    }

    [Fact]
    public void WriteExport_RootWithOneChild_HoldsBothSessionsRowsInOrderAndNoArchivedRows()
    {
        string path = CreateFixture();
        using IHarnessSqliteDatabase database = Source().Open(path);

        using JsonDocument export = JsonDocument.Parse(Export(database, "root-1"));
        JsonElement root = export.RootElement;

        Assert.Equal("root-1", root.GetProperty("session_id").GetString());
        Assert.Equal("Root one", root.GetProperty("title").GetString());
        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(["m1", "m4", "m2"], messages.EnumerateArray().Select(static m => m.GetProperty("id").GetString()));
        Assert.Equal(["root-1", "root-1", "child-1"], messages.EnumerateArray().Select(static m => m.GetProperty("session_id").GetString()));
        Assert.Equal(JsonValueKind.Object, messages[0].GetProperty("data").ValueKind);
        JsonElement parts = root.GetProperty("parts");
        Assert.Equal(["p1", "p2", "p4"], parts.EnumerateArray().Select(static p => p.GetProperty("id").GetString()));
        Assert.Equal(["root-1", "child-1", "root-1"], parts.EnumerateArray().Select(static p => p.GetProperty("session_id").GetString()));
    }

    [Fact]
    public void WriteExport_ThroughKiloMapper_YieldsTheFixturesTurnsWithoutProblems()
    {
        string path = CreateFixture();
        using IHarnessSqliteDatabase database = Source().Open(path);
        string export = Export(database, "root-1");

        NativeSourceUnit unit = new(
            new SourceUnit(
                HarnessId: KiloMapper.KiloSharedRuntimeHarnessId,
                SourceKey: path,
                ProviderId: "kilo",
                ParserId: "kilo-sqlite-reader",
                ParserContractVersion: "1",
                Format: "kilo-sqlite",
                SourceRootLabel: "kilo",
                RevisionToken: "3000-3",
                LastWriteUtc: DateTimeOffset.FromUnixTimeMilliseconds(3000),
                SizeBytes: Encoding.UTF8.GetByteCount(export)),
            export,
            Encoding.UTF8.GetByteCount(export),
            DateTimeOffset.FromUnixTimeMilliseconds(3000),
            null);

        CollectedBatch batch = new KiloMapper().Map(unit, DateTimeOffset.FromUnixTimeMilliseconds(4000));

        Assert.Empty(batch.Problems);
        Assert.Equal(2, batch.Records.Count);
        Assert.Contains(batch.Records, static r => r.Parts.Any(static p => p.PlainText == "hi"));
        Assert.Contains(batch.Records, static r => r.Parts.Any(static p => p.PlainText == "answer"));
        Assert.All(batch.Records, static r => Assert.Equal("root-1", r.SessionKey));
    }

    /// <summary>Creates the fixture: root-1 with child-1; an archived child, an archived root and root-2 with no messages.</summary>
    private string CreateFixture()
    {
        string path = Path.Combine(directory.Path, DatabaseFile);
        return SqliteFixtureDatabase.Create(path, Ddl, [
            Statement("INSERT INTO session (id, parent_id, title, time_created, time_updated, time_archived) VALUES (?1, NULL, ?2, 1, 1, NULL)", "root-1", "Root one"),
            Statement("INSERT INTO session (id, parent_id, title, time_created, time_updated, time_archived) VALUES (?1, ?2, ?3, 1, 1, NULL)", "child-1", "root-1", "Child"),
            Statement("INSERT INTO session (id, parent_id, title, time_created, time_updated, time_archived) VALUES (?1, ?2, ?3, 1, 1, 5)", "child-archived", "root-1", "Archived child"),
            Statement("INSERT INTO session (id, parent_id, title, time_created, time_updated, time_archived) VALUES (?1, NULL, ?2, 1, 1, 5)", "root-archived", "Archived root"),
            Statement("INSERT INTO session (id, parent_id, title, time_created, time_updated, time_archived) VALUES (?1, NULL, ?2, 1, 1, NULL)", "root-2", "Root two"),
            Statement("INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?4, ?5)", "m1", "root-1", 1000L, 1000L, "{\"role\":\"user\"}"),
            Statement("INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?4, ?5)", "m4", "root-1", 1500L, 1500L, "{\"role\":\"assistant\",\"modelID\":\"claude-opus-4-6\",\"tokens\":{\"input\":10,\"output\":5,\"reasoning\":0,\"cache\":{\"read\":0,\"write\":0}}}"),
            Statement("INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?4, ?5)", "m2", "child-1", 2000L, 3000L, "{\"role\":\"assistant\",\"modelID\":\"claude-opus-4-6\",\"tokens\":{\"input\":10,\"output\":5,\"reasoning\":0,\"cache\":{\"read\":0,\"write\":0}}}"),
            Statement("INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?4, ?5)", "m3", "child-archived", 2500L, 9000L, "{\"role\":\"assistant\"}"),
            Statement("INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", "p1", "m1", "root-1", 1000L, 1000L, "{\"type\":\"text\",\"text\":\"hi\"}"),
            Statement("INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", "p4", "m4", "root-1", 1500L, 1500L, "{\"type\":\"text\",\"text\":\"root answer\"}"),
            Statement("INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", "p2", "m2", "child-1", 2000L, 3000L, "{\"type\":\"text\",\"text\":\"answer\"}"),
            Statement("INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", "p3", "m3", "child-archived", 2500L, 9000L, "{\"type\":\"text\",\"text\":\"archived\"}"),
        ]);
    }

    private static SqliteFixtureStatement Statement(string sql, params object?[] values) =>
        new(sql, values);

    private MicrosoftDataSqliteHarnessSource Source() =>
        new(new HarnessSqliteLimits(), directory.Combine("cache"));

    private IReadOnlyList<SqliteConversation> ListWith(IHarnessSqliteDatabase database, string path, DateTimeOffset? since)
    {
        SqliteConversationContext context = new(
            database,
            path,
            KiloMapper.KiloSharedRuntimeHarnessId,
            since,
            directory.Path,
            new PhysicalHarnessFileSystem(),
            Source(),
            new List<CollectedProblem>());
        return [.. new KiloSqliteConversationSource().ListConversations(context)];
    }

    private static string Export(IHarnessSqliteDatabase database, string conversationId)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            new KiloSqliteConversationSource().WriteExport(database, conversationId, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
