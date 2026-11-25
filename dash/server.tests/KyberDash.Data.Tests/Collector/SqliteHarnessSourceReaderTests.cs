using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>Pins the generic SQLite reader against a stub conversation source and the in-memory database fake.</summary>
public class SqliteHarnessSourceReaderTests
{
    private const string Home = "/home/u";
    private const string RelativePath = "state/chat.db";
    private const string DbPath = "/home/u/state/chat.db";

    [Fact]
    public void Discover_ListsOneUnitPerConversation()
    {
        Rig rig = new();
        rig.Sqlite.AddDatabase(DbPath).AddTable("chats", "id");
        rig.Files.Add(DbPath, "");
        DateTimeOffset active = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        rig.Source.Conversations.Add(new SqliteConversation("a", "rev-a", active, "/home/u/c.json"));
        rig.Source.Conversations.Add(new SqliteConversation("b", "rev-b", null, null));

        SourceDiscoveryResult result = rig.Reader.Discover(rig.Context(since: active));

        Assert.Empty(result.Problems);
        Assert.Equal([DbPath + "#a", DbPath + "#b"], result.Sources.Select(unit => unit.SourceKey));
        SourceUnit first = result.Sources[0];
        Assert.Equal("kilo", first.HarnessId);
        Assert.Equal("prov", first.ProviderId);
        Assert.Equal("parser", first.ParserId);
        Assert.Equal("fmt", first.Format);
        Assert.Equal("7", first.ParserContractVersion);
        Assert.Equal("chat.db", first.SourceRootLabel);
        Assert.Equal("rev-a", first.RevisionToken);
        Assert.Equal(active, first.LastWriteUtc);
        Assert.Equal("/home/u/c.json", first.CompanionKey);
        Assert.Equal(SourceKind.File, first.Kind);
        Assert.Equal(active, rig.Source.SeenSinceUtc);
        Assert.Equal(DbPath, rig.Source.SeenDatabasePath);
    }

    [Fact]
    public void Discover_NoDatabaseFile_YieldsNothing()
    {
        Rig rig = new();

        SourceDiscoveryResult result = rig.Reader.Discover(rig.Context());

        Assert.Empty(result.Sources);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Discover_OpenFailure_RecordsOneUnreadableProblem()
    {
        Rig rig = new();
        rig.Files.Add(DbPath, "");

        SourceDiscoveryResult result = rig.Reader.Discover(rig.Context());

        Assert.Empty(result.Sources);
        CollectedProblem problem = Assert.Single(result.Problems);
        Assert.Equal("SQLITE_SOURCE_UNREADABLE", problem.Code);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal("kilo", problem.HarnessId);
        Assert.Equal(DbPath, problem.Location);
        Assert.Equal($"sqlite-source:SQLITE_SOURCE_UNREADABLE:kilo:{DbPath}", problem.ProblemKey);
    }

    [Fact]
    public void Discover_MissingColumn_RecordsSchemaProblemNamingTableAndColumn()
    {
        Rig rig = new();
        rig.Files.Add(DbPath, "");
        rig.Sqlite.AddDatabase(DbPath).AddTable("chats", "other");
        rig.Source.Conversations.Add(new SqliteConversation("a", "r", null, null));

        SourceDiscoveryResult result = rig.Reader.Discover(rig.Context());

        Assert.Empty(result.Sources);
        CollectedProblem problem = Assert.Single(result.Problems);
        Assert.Equal("SQLITE_SOURCE_SCHEMA_MISSING", problem.Code);
        Assert.Contains("chats", problem.Message, StringComparison.Ordinal);
        Assert.Contains("id", problem.Message, StringComparison.Ordinal);
        Assert.Equal($"sqlite-source:SQLITE_SOURCE_SCHEMA_MISSING:kilo:{DbPath}", problem.ProblemKey);
    }

    [Fact]
    public void Discover_ListingFailure_RecordsProblemInsteadOfThrowing()
    {
        Rig rig = new();
        rig.Files.Add(DbPath, "");
        rig.Sqlite.AddDatabase(DbPath).AddTable("chats", "id");
        rig.Source.ListFailure = new InvalidDataException("SQLITE_SOURCE_UNREADABLE: bad page");

        SourceDiscoveryResult result = rig.Reader.Discover(rig.Context());

        Assert.Empty(result.Sources);
        Assert.Equal("SQLITE_SOURCE_UNREADABLE", Assert.Single(result.Problems).Code);
    }

    [Fact]
    public void Read_ReturnsTheStubExport()
    {
        Rig rig = new();
        rig.Sqlite.AddDatabase(DbPath).AddTable("chats", "id");
        SourceUnit unit = rig.Unit(DbPath + "#a");

        NativeSourceUnit? native = rig.Reader.Read(unit, rig.Files);

        Assert.NotNull(native);
        Assert.Equal("{\"id\":\"a\"}", native.RawText);
        Assert.Equal(10, native.SizeBytes);
        Assert.Null(native.CompanionText);
        Assert.Equal("a", rig.Source.ExportedId);
    }

    [Fact]
    public void Read_ExportOverCap_ThrowsCapExceeded()
    {
        Rig rig = new(new HarnessSqliteLimits(ExportCapBytes: 4));
        rig.Sqlite.AddDatabase(DbPath);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => rig.Reader.Read(rig.Unit(DbPath + "#a"), rig.Files));

        Assert.StartsWith("SQLITE_EXPORT_CAP_EXCEEDED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DatabaseUnreadable_ThrowsIoException()
    {
        Rig rig = new();

        IOException error = Assert.Throws<IOException>(() => rig.Reader.Read(rig.Unit(DbPath + "#a"), rig.Files));

        Assert.StartsWith("SQLITE_SOURCE_UNREADABLE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_CompanionKey_ReadsCompanionThroughFileSystem()
    {
        Rig rig = new();
        rig.Sqlite.AddDatabase(DbPath);
        rig.Files.Add("/home/u/c.json", "{\"c\":1}");
        SourceUnit unit = rig.Unit(DbPath + "#a") with { CompanionKey = "/home/u/c.json" };

        NativeSourceUnit? native = rig.Reader.Read(unit, rig.Files);

        Assert.Equal("{\"c\":1}", native?.CompanionText);
    }

    [Fact]
    public void Read_SourceKeyWithoutHash_ThrowsInvalidData()
    {
        Rig rig = new();

        Assert.Throws<InvalidDataException>(() => rig.Reader.Read(rig.Unit(DbPath), rig.Files));
    }

    [Fact]
    public void Read_ConversationIdMayContainHash_SplitsAtLastHash()
    {
        Rig rig = new();
        rig.Sqlite.AddDatabase("/home/u/a#b.db#c");

        _ = rig.Reader.Read(rig.Unit("/home/u/a#b.db#c#d"), rig.Files);

        Assert.Equal("d", rig.Source.ExportedId);
    }

    [Fact]
    public void Read_ExportOverCap_StopsTheExportBeforeItIsFullyWritten()
    {
        const int Chunks = 200;
        Rig rig = new(new HarnessSqliteLimits(ExportCapBytes: 16 * 1024));
        rig.Sqlite.AddDatabase(DbPath);
        string chunk = new('x', 1024);
        int written = 0;
        rig.Source.Export = writer =>
        {
            writer.WriteStartArray();
            for (int i = 0; i < Chunks; i++)
            {
                writer.WriteStringValue(chunk);
                written++;
            }

            writer.WriteEndArray();
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => rig.Reader.Read(rig.Unit(DbPath + "#a"), rig.Files));

        Assert.StartsWith("SQLITE_EXPORT_CAP_EXCEEDED", error.Message, StringComparison.Ordinal);
        Assert.InRange(written, 1, Chunks - 1);
    }

    [Fact]
    public void Read_ExportExactlyAtCap_IsAccepted()
    {
        Rig rig = new(new HarnessSqliteLimits(ExportCapBytes: 10));
        rig.Sqlite.AddDatabase(DbPath);

        NativeSourceUnit? native = rig.Reader.Read(rig.Unit(DbPath + "#a"), rig.Files);

        Assert.Equal("{\"id\":\"a\"}", native?.RawText);
        Assert.Equal(10, native?.SizeBytes);
    }

    [Fact]
    public void WriteJsonColumn_WritesRawJsonStringsNumbersAndNull()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "obj", "{\"a\":1}");
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "arr", " [1,2]");
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "bad", "{not json");
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "text", "hello");
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "int", 5L);
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "real", 1.5d);
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "nul", null);
            writer.WriteEndObject();
        }

        Assert.Equal(
            "{\"obj\":{\"a\":1},\"arr\":[1,2],\"bad\":\"{not json\",\"text\":\"hello\",\"int\":5,\"real\":1.5,\"nul\":null}",
            Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void WriteJsonColumn_WritesNullForNonFiniteReals()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "nan", double.NaN);
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "pos", double.PositiveInfinity);
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "neg", double.NegativeInfinity);
            writer.WriteEndObject();
        }

        Assert.Equal("{\"nan\":null,\"pos\":null,\"neg\":null}", Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void WriteJsonColumn_WritesByteArrayAsBase64String()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            SqliteHarnessSourceReader.WriteJsonColumn(writer, "blob", new byte[] { 1, 2, 3, 250 });
            writer.WriteEndObject();
        }

        Assert.Equal("{\"blob\":\"" + Convert.ToBase64String(new byte[] { 1, 2, 3, 250 }) + "\"}", Encoding.UTF8.GetString(stream.ToArray()));
    }

    private sealed class Rig
    {
        public Rig(HarnessSqliteLimits? limits = null)
        {
            Reader = new SqliteHarnessSourceReader(
                "kilo", "prov", "parser", "fmt", "7", [RelativePath], Source, Sqlite, limits ?? new HarnessSqliteLimits());
        }

        public InMemoryHarnessSqliteSource Sqlite { get; } = new();

        public StubConversationSource Source { get; } = new();

        public MemoryFiles Files { get; } = new();

        public SqliteHarnessSourceReader Reader { get; }

        public SourceDiscoveryContext Context(DateTimeOffset? since = null) => new(Home, "/state", Files, since);

        public SourceUnit Unit(string sourceKey) =>
            new("kilo", sourceKey, "prov", "parser", "7", "fmt", "chat.db", "rev");
    }

    private sealed class StubConversationSource : ISqliteConversationSource
    {
        public List<SqliteConversation> Conversations { get; } = [];

        public Exception? ListFailure { get; set; }

        public DateTimeOffset? SeenSinceUtc { get; private set; }

        public string? SeenDatabasePath { get; private set; }

        public string? ExportedId { get; private set; }

        public Action<Utf8JsonWriter>? Export { get; set; }

        public IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredColumns { get; } =
            new Dictionary<string, IReadOnlyList<string>> { ["chats"] = ["id"] };

        public IEnumerable<SqliteConversation> ListConversations(SqliteConversationContext context)
        {
            SeenSinceUtc = context.SinceUtc;
            SeenDatabasePath = context.DatabasePath;
            if (ListFailure is not null)
            {
                throw ListFailure;
            }

            return Conversations;
        }

        public void WriteExport(IHarnessSqliteDatabase database, string conversationId, Utf8JsonWriter writer)
        {
            ExportedId = conversationId;
            if (Export is not null)
            {
                Export(writer);
                return;
            }

            writer.WriteStartObject();
            writer.WriteString("id", "a");
            writer.WriteEndObject();
        }
    }

    private sealed class MemoryFiles : IHarnessFileSystem
    {
        private readonly Dictionary<string, string> files = new(StringComparer.Ordinal);

        public void Add(string path, string content) => files[path] = content;

        public bool DirectoryExists(string path) => false;

        public bool FileExists(string path) => files.ContainsKey(path);

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive) => [];

        public IEnumerable<string> EnumerateDirectories(string directoryPath) => [];

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath) => files[filePath];

        public string GetFullPath(string path) => path;
    }
}
