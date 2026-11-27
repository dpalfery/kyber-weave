using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Cursor;
using KyberDash.Data.Tests.Collector;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Real-file tests for <see cref="CursorSqliteConversationSource"/>: a <c>state.vscdb</c>-shaped SQLite file
/// is written with <see cref="SqliteFixtureDatabase"/> and read through the Microsoft.Data.Sqlite adapter, so
/// the key range, the JSON projection and the 500-character cut are proved by SQLite itself.
/// </summary>
public sealed class CursorSqliteConversationSourceSqliteTests : IDisposable
{
    private const string Ddl = "CREATE TABLE cursorDiskKV (key TEXT UNIQUE, value BLOB);";
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ListConversations_OneUnitPerComposerFromBubbleKeysIgnoringOtherKeys()
    {
        // rowid 1: composerData for composer-1; 2-3: composer-1 bubbles; 4: composer-2 bubble with no composerData row.
        string path = CreateDatabase(
            Statement("composerData:composer-1", "{}"),
            Statement("bubbleId:composer-1:b1", Bubble(1, 10, 5)),
            Statement("bubbleId:composer-1:b2", Bubble(2, 20, 8)),
            Statement("bubbleId:composer-2:b1", Bubble(1, 30, 9)),
            Statement("checkpointId:composer-1", "{}"),
            Statement("bubbleId:", "{}"));

        List<SqliteConversation> conversations = ListConversations(path);

        Assert.Equal(2, conversations.Count);
        Assert.Equal("composer-1", conversations[0].Id);
        Assert.Equal("2-3", conversations[0].RevisionToken);
        Assert.Equal("composer-2", conversations[1].Id);
        Assert.Equal("1-4", conversations[1].RevisionToken);
    }

    [Fact]
    public void WriteExport_RowsInRowidOrderWithCutTextAndNullsForMissingValues()
    {
        string longText = new('a', 600);
        string path = CreateDatabase(
            Statement("bubbleId:composer-1:b1", Bubble(1, 10, 5, text: longText, model: "claude-sonnet-4-5")),
            Statement("bubbleId:composer-1:b2", "{\"type\":2,\"text\":\"reply\",\"createdAt\":\"2026-09-22T10:02:00.000Z\",\"codeBlocks\":[{\"language\":\"csharp\"}]}"),
            Statement("bubbleId:composer-2:b1", Bubble(1, 1, 1)));

        string json = Export(path, "composer-1");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement[] bubbles = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, bubbles.Length);

        Assert.Equal("bubbleId:composer-1:b1", bubbles[0].GetProperty("bubble_key").GetString());
        Assert.Equal(10, bubbles[0].GetProperty("input_tokens").GetInt64());
        Assert.Equal(5, bubbles[0].GetProperty("output_tokens").GetInt64());
        Assert.Equal("claude-sonnet-4-5", bubbles[0].GetProperty("model").GetString());
        Assert.Equal(500, bubbles[0].GetProperty("user_text").GetString()!.Length);
        Assert.Equal(600, bubbles[0].GetProperty("text_length").GetInt64());

        Assert.Equal("bubbleId:composer-1:b2", bubbles[1].GetProperty("bubble_key").GetString());
        Assert.Equal(JsonValueKind.Null, bubbles[1].GetProperty("input_tokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, bubbles[1].GetProperty("output_tokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, bubbles[1].GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Array, bubbles[1].GetProperty("code_blocks").ValueKind);
    }

    [Fact]
    public void WriteExport_ExportRoundTripsThroughCursorMapperToReportedTokens()
    {
        string path = CreateDatabase(
            Statement("bubbleId:composer-1:11111111", Bubble(1, 60, 0, text: "summarize this file", model: null)),
            Statement("bubbleId:composer-1:22222222", Bubble(2, 120, 45, text: "Here is the summary.", model: "claude-sonnet-4-5")));

        string json = Export(path, "composer-1");
        NativeSourceUnit unit = new(
            Unit: new SourceUnit(
                HarnessId: "cursor",
                SourceKey: path,
                ProviderId: "cursor",
                ParserId: "cursor-vscdb-reader",
                ParserContractVersion: "1",
                Format: "cursor-vscdb",
                SourceRootLabel: "cursor",
                RevisionToken: "2-3",
                LastWriteUtc: BaselineUtc,
                SizeBytes: Encoding.UTF8.GetByteCount(json)),
            RawText: json,
            SizeBytes: Encoding.UTF8.GetByteCount(json),
            LastWriteUtc: BaselineUtc);

        CollectedBatch batch = new CursorMapper().Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedRecord turn = Assert.Single(batch.Records);
        Assert.Equal(180, turn.ReportedInputTokens);
        Assert.Equal(45, turn.ReportedOutputTokens);
    }

    [Fact]
    public void WriteExport_ComposerWithNoRowsThrowsInvalidData()
    {
        string path = CreateDatabase(Statement("bubbleId:composer-1:b1", Bubble(1, 1, 1)));

        Assert.Throws<InvalidDataException>(() => Export(path, "composer-gone"));
    }

    private string CreateDatabase(params SqliteFixtureStatement[] statements) =>
        SqliteFixtureDatabase.Create(Path.Combine(directory.Path, "state.vscdb"), Ddl, statements);

    private static SqliteFixtureStatement Statement(string key, string value) =>
        new("INSERT INTO cursorDiskKV (key, value) VALUES (?1, ?2);", [key, value]);

    private static string Bubble(long type, long inputTokens, long outputTokens, string text = "hi", string? model = null) =>
        "{\"type\":" + type
        + ",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text)
        + ",\"createdAt\":\"2026-09-22T10:01:00.000Z\""
        + ",\"tokenCount\":{\"inputTokens\":" + inputTokens + ",\"outputTokens\":" + outputTokens + "}"
        + ",\"modelInfo\":" + (model is null ? "{}" : "{\"modelName\":" + System.Text.Json.JsonSerializer.Serialize(model) + "}")
        + "}";

    private List<SqliteConversation> ListConversations(string path)
    {
        using OpenedSource opened = Open(path);
        return new CursorSqliteConversationSource().ListConversations(opened.Context).ToList();
    }

    private string Export(string path, string composerId)
    {
        using OpenedSource opened = Open(path);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            new CursorSqliteConversationSource().WriteExport(opened.Context.Database, composerId, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private OpenedSource Open(string path)
    {
        MicrosoftDataSqliteHarnessSource sqlite = new(new HarnessSqliteLimits(), Path.Combine(directory.Path, "cache"));
        return new OpenedSource(sqlite, path);
    }

    /// <summary>Holds an open adapter database and the context a conversation source reads it through.</summary>
    private sealed class OpenedSource : IDisposable
    {
        private readonly IHarnessSqliteDatabase database;

        public OpenedSource(IHarnessSqliteSource sqlite, string path)
        {
            database = sqlite.Open(path);
            Context = new SqliteConversationContext(
                Database: database,
                DatabasePath: path,
                HarnessId: "cursor",
                SinceUtc: null,
                HomeDirectory: Path.GetDirectoryName(path) ?? string.Empty,
                FileSystem: new NoFiles(),
                Sqlite: sqlite,
                Problems: new List<CollectedProblem>());
        }

        public SqliteConversationContext Context { get; }

        public void Dispose() => database.Dispose();
    }

    private sealed class NoFiles : IHarnessFileSystem
    {
        public bool DirectoryExists(string path) => false;

        public bool FileExists(string path) => false;

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive) => [];

        public IEnumerable<string> EnumerateDirectories(string directoryPath) => [];

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath) => string.Empty;

        public string ReadAllText(string filePath, long maxBytes)
        {
            string text = ReadAllText(filePath);
            return System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes
                ? text
                : throw new InvalidDataException($"'{filePath}' holds more than {maxBytes} bytes.");
        }

        public string GetFullPath(string path) => path;
    }
}
