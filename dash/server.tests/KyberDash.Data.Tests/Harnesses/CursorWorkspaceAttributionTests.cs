using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Cursor;
using KyberDash.Data.Tests.Collector;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// In-memory tests for the composer-to-workspace map <see cref="CursorSqliteConversationSource"/> builds
/// from each <c>workspaceStorage/&lt;hash&gt;/state.vscdb</c> <c>ItemTable</c> (D14).
/// </summary>
public class CursorWorkspaceAttributionTests
{
    private const string UserDirectory = "/fake/Cursor/User";
    private const string GlobalPath = UserDirectory + "/globalStorage/state.vscdb";
    private const string StorageRoot = UserDirectory + "/workspaceStorage";

    [Fact]
    public void ListConversations_AttributesEachComposerToItsWorkspaceFile()
    {
        InMemoryHarnessSqliteSource sqlite = GlobalWithComposers("c1", "c2", "c3");
        AddWorkspace(sqlite, "aaa", """{"allComposers":[{"composerId":"c1"}]}""");
        AddWorkspace(sqlite, "bbb", """{"allComposers":[{"composerId":"c2"}]}""");
        List<CollectedProblem> problems = [];

        Dictionary<string, string?> keys = List(sqlite, problems, "aaa", "bbb");

        Assert.Equal(StorageRoot + "/aaa/workspace.json", keys["c1"]);
        Assert.Equal(StorageRoot + "/bbb/workspace.json", keys["c2"]);
        Assert.Null(keys["c3"]);
        Assert.Empty(problems);
    }

    [Fact]
    public void ListConversations_SkipsWorkspaceWithMissingRowAndWarnsOnce()
    {
        InMemoryHarnessSqliteSource sqlite = GlobalWithComposers("c1", "c2");
        sqlite.AddDatabase(StorageRoot + "/aaa/state.vscdb");
        AddWorkspace(sqlite, "bbb", """{"allComposers":[{"composerId":"c2"}]}""");
        List<CollectedProblem> problems = [];

        Dictionary<string, string?> keys = List(sqlite, problems, "aaa", "bbb");

        Assert.Null(keys["c1"]);
        Assert.Equal(StorageRoot + "/bbb/workspace.json", keys["c2"]);
        CollectedProblem problem = Assert.Single(problems);
        Assert.Equal("warning", problem.Severity);
    }

    [Fact]
    public void ListConversations_SkipsWorkspaceWithMalformedJsonAndWarnsOnce()
    {
        InMemoryHarnessSqliteSource sqlite = GlobalWithComposers("c1", "c2");
        AddWorkspace(sqlite, "aaa", "{not json");
        AddWorkspace(sqlite, "bbb", """{"allComposers":[{"composerId":"c2"}]}""");
        List<CollectedProblem> problems = [];

        Dictionary<string, string?> keys = List(sqlite, problems, "aaa", "bbb");

        Assert.Null(keys["c1"]);
        Assert.Equal(StorageRoot + "/bbb/workspace.json", keys["c2"]);
        Assert.Equal("warning", Assert.Single(problems).Severity);
    }

    [Fact]
    public void ListConversations_SkipsWorkspaceThatFailsToOpenAndWarnsOnce()
    {
        InMemoryHarnessSqliteSource sqlite = GlobalWithComposers("c1");
        AddWorkspace(sqlite, "aaa", """{"allComposers":[{"composerId":"c1"}]}""");
        sqlite.FailOpen(StorageRoot + "/aaa/state.vscdb");
        List<CollectedProblem> problems = [];

        Dictionary<string, string?> keys = List(sqlite, problems, "aaa");

        Assert.Null(keys["c1"]);
        Assert.Single(problems);
    }

    [Fact]
    public void ListConversations_WorkspaceWithoutWorkspaceFileLeavesComposerUnattributed()
    {
        InMemoryHarnessSqliteSource sqlite = GlobalWithComposers("c1");
        AddWorkspace(sqlite, "aaa", """{"allComposers":[{"composerId":"c1"}]}""");
        List<CollectedProblem> problems = [];

        Dictionary<string, string?> keys = ListWithout(sqlite, problems, "aaa", "aaa");

        Assert.Null(keys["c1"]);
        Assert.Empty(problems);
    }

    private static InMemoryHarnessSqliteSource GlobalWithComposers(params string[] composerIds)
    {
        InMemoryHarnessSqliteSource sqlite = new();
        FakeDatabase global = sqlite.AddDatabase(GlobalPath);
        foreach (string id in composerIds)
        {
            global.AddRow("cursor-composers", new Dictionary<string, object?>
            {
                ["composer_id"] = id,
                ["bubble_count"] = 1L,
                ["max_rowid"] = 1L,
            });
        }

        return sqlite;
    }

    private static void AddWorkspace(InMemoryHarnessSqliteSource sqlite, string hash, string composerDataJson) =>
        sqlite.AddDatabase(StorageRoot + "/" + hash + "/state.vscdb")
            .AddRow("cursor-workspace-composers", new Dictionary<string, object?> { ["value"] = composerDataJson });

    private static Dictionary<string, string?> List(
        InMemoryHarnessSqliteSource sqlite,
        List<CollectedProblem> problems,
        params string[] hashes) =>
        ListWithout(sqlite, problems, null, hashes);

    private static Dictionary<string, string?> ListWithout(
        InMemoryHarnessSqliteSource sqlite,
        List<CollectedProblem> problems,
        string? withoutWorkspaceFile,
        params string[] hashes)
    {
        HashSet<string> files = [];
        foreach (string hash in hashes)
        {
            files.Add(StorageRoot + "/" + hash + "/state.vscdb");
            if (hash != withoutWorkspaceFile)
            {
                files.Add(StorageRoot + "/" + hash + "/workspace.json");
            }
        }

        using IHarnessSqliteDatabase database = sqlite.Open(GlobalPath);
        SqliteConversationContext context = new(
            Database: database,
            DatabasePath: GlobalPath,
            HarnessId: "cursor",
            SinceUtc: null,
            HomeDirectory: "/fake",
            FileSystem: new StorageFiles(hashes, files),
            Sqlite: sqlite,
            Problems: problems);

        return new CursorSqliteConversationSource()
            .ListConversations(context)
            .ToDictionary(conversation => conversation.Id, conversation => conversation.CompanionKey, StringComparer.Ordinal);
    }

    private sealed class StorageFiles(string[] hashes, HashSet<string> files) : IHarnessFileSystem
    {
        public bool DirectoryExists(string path) => path == StorageRoot;

        public bool FileExists(string path) => files.Contains(path);

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive) => [];

        public IEnumerable<string> EnumerateDirectories(string directoryPath) =>
            directoryPath == StorageRoot ? hashes.Select(hash => StorageRoot + "/" + hash) : [];

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath) => string.Empty;

        public string GetFullPath(string path) => path;
    }
}
