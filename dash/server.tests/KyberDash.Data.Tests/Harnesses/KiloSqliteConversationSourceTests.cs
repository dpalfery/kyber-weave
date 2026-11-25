using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Kilo;
using KyberDash.Data.Tests.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// In-memory tests for <see cref="KiloSqliteConversationSource"/>: the canned rows come from
/// <see cref="InMemoryHarnessSqliteSource"/>, so the SQL text is not exercised here (the real-file tests do that).
/// </summary>
public class KiloSqliteConversationSourceTests
{
    private const string DbPath = "/fake-home/.local/share/kilo/kilo.db";

    private static readonly long RootUpdatedMs = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void RequiredColumns_NamesTheColumnsEachTableExportReads()
    {
        KiloSqliteConversationSource source = new();

        Assert.Equal(["id", "parent_id", "title", "time_archived"], source.RequiredColumns["session"]);
        Assert.Equal(["id", "session_id", "time_created", "time_updated", "data"], source.RequiredColumns["message"]);
        Assert.Equal(["id", "message_id", "session_id", "time_created", "data"], source.RequiredColumns["part"]);
    }

    [Fact]
    public void ListConversations_ReportsRevisionFromTreeMaxUpdateAndMessageCount()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase database = sqlite.AddDatabase(DbPath);
        database.AddRow("kilo-roots", Row(("id", "root-1"), ("title", "Root")));
        database.AddRow("kilo-revision", Row(("max_updated", RootUpdatedMs), ("message_count", 4L)));

        SqliteConversation conversation = Assert.Single(ListWith(sqlite, since: null));

        Assert.Equal("root-1", conversation.Id);
        Assert.Equal($"{RootUpdatedMs}-4", conversation.RevisionToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(RootUpdatedMs), conversation.LastActivityUtc);
        Assert.Null(conversation.CompanionKey);
    }

    [Fact]
    public void ListConversations_SinceUtcSkipsATreeWhoseLatestUpdateIsOlder()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase database = sqlite.AddDatabase(DbPath);
        database.AddRow("kilo-roots", Row(("id", "root-1"), ("title", "Root")));
        database.AddRow("kilo-revision", Row(("max_updated", RootUpdatedMs), ("message_count", 1L)));

        DateTimeOffset later = DateTimeOffset.FromUnixTimeMilliseconds(RootUpdatedMs + 1);
        DateTimeOffset exact = DateTimeOffset.FromUnixTimeMilliseconds(RootUpdatedMs);

        Assert.Empty(ListWith(sqlite, since: later));
        Assert.Single(ListWith(sqlite, since: exact));
    }

    [Fact]
    public void WriteExport_HoldsRootAndChildRowsEachWithItsOwnSessionId()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase database = sqlite.AddDatabase(DbPath);
        database.AddRow("kilo-root", Row(("id", "root-1"), ("title", "Root title")));
        database.AddRow("kilo-messages", Row(("id", "m-root"), ("session_id", "root-1"), ("time_created", 1000L), ("data", "{\"role\":\"user\"}")));
        database.AddRow("kilo-messages", Row(("id", "m-child"), ("session_id", "child-1"), ("time_created", 2000L), ("data", "{\"role\":\"assistant\"}")));
        database.AddRow("kilo-parts", Row(("id", "p-root"), ("message_id", "m-root"), ("session_id", "root-1"), ("time_created", 1000L), ("data", "{\"type\":\"text\",\"text\":\"hi\"}")));
        database.AddRow("kilo-parts", Row(("id", "p-child"), ("message_id", "m-child"), ("session_id", "child-1"), ("time_created", 2000L), ("data", "{\"type\":\"text\",\"text\":\"answer\"}")));

        using JsonDocument export = JsonDocument.Parse(Export(sqlite, "root-1"));
        JsonElement root = export.RootElement;

        Assert.Equal("root-1", root.GetProperty("session_id").GetString());
        Assert.Equal("Root title", root.GetProperty("title").GetString());
        JsonElement messages = root.GetProperty("messages");
        Assert.Equal(["root-1", "child-1"], messages.EnumerateArray().Select(static m => m.GetProperty("session_id").GetString()));
        Assert.Equal(["m-root", "m-child"], messages.EnumerateArray().Select(static m => m.GetProperty("id").GetString()));
        JsonElement parts = root.GetProperty("parts");
        Assert.Equal(["root-1", "child-1"], parts.EnumerateArray().Select(static p => p.GetProperty("session_id").GetString()));
        Assert.Equal(["m-root", "m-child"], parts.EnumerateArray().Select(static p => p.GetProperty("message_id").GetString()));
    }

    [Fact]
    public void WriteExport_WritesJsonDataRawAndOtherDataAsAString()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase database = sqlite.AddDatabase(DbPath);
        database.AddRow("kilo-root", Row(("id", "root-1"), ("title", null)));
        database.AddRow("kilo-messages", Row(("id", "m-1"), ("session_id", "root-1"), ("time_created", 1000L), ("data", "{\"role\":\"user\"}")));
        database.AddRow("kilo-parts", Row(("id", "p-1"), ("message_id", "m-1"), ("session_id", "root-1"), ("time_created", 1000L), ("data", "plain text")));

        using JsonDocument export = JsonDocument.Parse(Export(sqlite, "root-1"));

        Assert.Equal(JsonValueKind.Object, export.RootElement.GetProperty("messages")[0].GetProperty("data").ValueKind);
        Assert.Equal("plain text", export.RootElement.GetProperty("parts")[0].GetProperty("data").GetString());
        Assert.Equal(JsonValueKind.Null, export.RootElement.GetProperty("title").ValueKind);
    }

    [Fact]
    public void WriteExport_UnknownRootRefusesWithInvalidData()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DbPath);

        Assert.Throws<InvalidDataException>(() => Export(sqlite, "gone"));
    }

    private static IReadOnlyList<SqliteConversation> ListWith(InMemoryHarnessSqliteSource sqlite, DateTimeOffset? since)
    {
        using IHarnessSqliteDatabase database = sqlite.Open(DbPath);
        SqliteConversationContext context = new(
            database,
            DbPath,
            KiloMapper.KiloSharedRuntimeHarnessId,
            since,
            "/fake-home",
            new PhysicalHarnessFileSystem(),
            sqlite,
            new List<CollectedProblem>());
        return [.. new KiloSqliteConversationSource().ListConversations(context)];
    }

    private static string Export(InMemoryHarnessSqliteSource sqlite, string conversationId)
    {
        using IHarnessSqliteDatabase database = sqlite.Open(DbPath);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            new KiloSqliteConversationSource().WriteExport(database, conversationId, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
        cells.ToDictionary(static cell => cell.Column, static cell => cell.Value, StringComparer.Ordinal);
}
