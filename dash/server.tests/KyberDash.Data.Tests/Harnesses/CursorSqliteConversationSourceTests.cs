using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Cursor;
using KyberDash.Data.Tests.Collector;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// In-memory unit tests for <see cref="CursorSqliteConversationSource"/>: the canned rows an
/// <see cref="IHarnessSqliteDatabase"/> returns are mapped to composers, revisions and the D10 export.
/// The SQL itself (key range, <c>json_extract</c>, the 500-character cut) is proved against a real
/// file in <c>CursorSqliteConversationSourceSqliteTests</c>.
/// </summary>
public class CursorSqliteConversationSourceTests
{
    private const string DatabasePath = "/fake/Library/Application Support/Cursor/User/globalStorage/state.vscdb";

    [Fact]
    public void ListConversations_MapsEachComposerToItsBubbleCountAndMaxRowid()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase fake = sqlite.AddDatabase(DatabasePath);
        fake.AddRow("cursor-composers", new Dictionary<string, object?>
        {
            ["composer_id"] = "composer-1",
            ["bubble_count"] = 3L,
            ["max_rowid"] = 17L,
        });

        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);
        List<SqliteConversation> conversations = new CursorSqliteConversationSource()
            .ListConversations(Context(sqlite, database, since: null))
            .ToList();

        SqliteConversation conversation = Assert.Single(conversations);
        Assert.Equal("composer-1", conversation.Id);
        Assert.Equal("3-17", conversation.RevisionToken);
        Assert.Null(conversation.LastActivityUtc);
        Assert.Null(conversation.CompanionKey);
    }

    [Fact]
    public void ListConversations_CorruptRow_RecordsAProblemAndListsTheOthers()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase fake = sqlite.AddDatabase(DatabasePath);
        fake.AddRow("cursor-composers", new Dictionary<string, object?>
        {
            ["composer_id"] = null,
            ["bubble_count"] = 1L,
            ["max_rowid"] = 2L,
        });
        fake.AddRow("cursor-composers", new Dictionary<string, object?>
        {
            ["composer_id"] = "composer-2",
            ["bubble_count"] = "not-a-number",
            ["max_rowid"] = 3L,
        });
        fake.AddRow("cursor-composers", new Dictionary<string, object?>
        {
            ["composer_id"] = "composer-3",
            ["bubble_count"] = 4L,
            ["max_rowid"] = 9L,
        });
        List<CollectedProblem> problems = [];

        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);
        List<SqliteConversation> conversations = [.. new CursorSqliteConversationSource()
            .ListConversations(Context(sqlite, database, since: null) with { Problems = problems })];

        Assert.Equal("composer-3", Assert.Single(conversations).Id);
        Assert.Equal(2, problems.Count);
        Assert.All(problems, problem => Assert.Equal(DatabasePath, problem.Location));
        Assert.Equal(2, problems.Select(problem => problem.ProblemKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ListConversations_DoesNotFilterBySinceUtc()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase fake = sqlite.AddDatabase(DatabasePath);
        fake.AddRow("cursor-composers", new Dictionary<string, object?>
        {
            ["composer_id"] = "composer-old",
            ["bubble_count"] = 1L,
            ["max_rowid"] = 2L,
        });

        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);
        List<SqliteConversation> conversations = new CursorSqliteConversationSource()
            .ListConversations(Context(sqlite, database, since: new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            .ToList();

        Assert.Equal("composer-old", Assert.Single(conversations).Id);
    }

    [Fact]
    public void WriteExport_WritesOneObjectPerBubbleAndNullForEveryMissingValue()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase fake = sqlite.AddDatabase(DatabasePath);
        fake.AddRow("cursor-bubbles", new Dictionary<string, object?>
        {
            ["bubble_key"] = "bubbleId:composer-1:b1",
            ["input_tokens"] = null,
            ["output_tokens"] = null,
            ["model"] = null,
            ["created_at"] = "2026-09-22T10:01:00.000Z",
            ["request_id"] = null,
            ["user_text"] = "[1, 2] is not a list of words here",
            ["text_length"] = 35L,
            ["bubble_type"] = 1L,
            ["context_window"] = null,
            ["code_blocks"] = null,
        });

        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);
        string json = Export(database, "composer-1");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement bubble = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("bubbleId:composer-1:b1", bubble.GetProperty("bubble_key").GetString());
        Assert.Equal(JsonValueKind.Null, bubble.GetProperty("input_tokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, bubble.GetProperty("output_tokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, bubble.GetProperty("model").ValueKind);
        Assert.Equal(JsonValueKind.Null, bubble.GetProperty("request_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, bubble.GetProperty("context_window").ValueKind);
        Assert.Equal(JsonValueKind.Null, bubble.GetProperty("code_blocks").ValueKind);
        Assert.Equal("[1, 2] is not a list of words here", bubble.GetProperty("user_text").GetString());
        Assert.Equal(35, bubble.GetProperty("text_length").GetInt64());
        Assert.Equal(1, bubble.GetProperty("bubble_type").GetInt64());
    }

    [Fact]
    public void WriteExport_ConversationWithNoRowsThrowsInvalidData()
    {
        InMemoryHarnessSqliteSource sqlite = new();
        sqlite.AddDatabase(DatabasePath);

        using IHarnessSqliteDatabase database = sqlite.Open(DatabasePath);

        Assert.Throws<InvalidDataException>(() => Export(database, "composer-gone"));
    }

    private static SqliteConversationContext Context(
        IHarnessSqliteSource sqlite,
        IHarnessSqliteDatabase database,
        DateTimeOffset? since) => new(
        Database: database,
        DatabasePath: DatabasePath,
        HarnessId: "cursor",
        SinceUtc: since,
        HomeDirectory: "/fake",
        FileSystem: new NoFiles(),
        Sqlite: sqlite,
        Problems: new List<CollectedProblem>());

    private static string Export(IHarnessSqliteDatabase database, string composerId)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            new CursorSqliteConversationSource().WriteExport(database, composerId, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
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
