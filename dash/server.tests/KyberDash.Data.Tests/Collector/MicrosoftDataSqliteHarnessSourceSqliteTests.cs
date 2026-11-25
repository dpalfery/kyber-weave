using System.Security.Cryptography;
using KyberDash.Core.Repositories;
using KyberDash.Data.Collector.Sources;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Proves <see cref="MicrosoftDataSqliteHarnessSource"/> against real SQLite files: streaming, column
/// discovery, leaving the source untouched, the immutable and cache-copy fallbacks in a read-only
/// directory, copy reuse and replacement, and the refusal codes.
/// </summary>
public class MicrosoftDataSqliteHarnessSourceSqliteTests : IDisposable
{
    private const string Ddl = "CREATE TABLE messages (id INTEGER PRIMARY KEY, role TEXT NOT NULL, body TEXT);";

    private readonly TempDirectory directory = new();
    private readonly List<string> sealedDirectories = [];

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (string sealedDirectory in sealedDirectories)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(sealedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Query_StreamsRowsInOrderWithBoundParameters()
    {
        string path = CreateSource(directory.Path, 3, SqliteFixtureJournal.Rollback);
        using IHarnessSqliteDatabase database = Source().Open(path);

        List<IReadOnlyDictionary<string, object?>> rows = [.. database.Query(new HarnessSqliteQuery(
            "messages-from",
            "SELECT '?' AS marker, id, role, body FROM messages WHERE id >= ? AND role <> ? ORDER BY id",
            [2L, "none"]))];

        Assert.Equal([2L, 3L], rows.Select(row => row["id"]));
        Assert.All(rows, row => Assert.Equal("?", row["marker"]));
        Assert.Equal("assistant", rows[0]["role"]);
        Assert.Null(rows[1]["body"]);
    }

    [Fact]
    public void Query_ParameterCountThatDoesNotMatchTheMarkers_IsRefused()
    {
        string path = CreateSource(directory.Path, 1, SqliteFixtureJournal.Rollback);
        using IHarnessSqliteDatabase database = Source().Open(path);

        Assert.Throws<ArgumentException>(() => database.Query(
            new HarnessSqliteQuery("mismatch", "SELECT id FROM messages WHERE id = ?", [])).ToList());
    }

    [Fact]
    public void GetColumns_ListsTheTableColumnsAndIsEmptyForAMissingTable()
    {
        string path = CreateSource(directory.Path, 1, SqliteFixtureJournal.Rollback);
        using IHarnessSqliteDatabase database = Source().Open(path);

        Assert.Equal(new HashSet<string>(["id", "role", "body"]), database.GetColumns("messages"));
        Assert.Empty(database.GetColumns("no_such_table"));
    }

    [Fact]
    public void Open_LeavesTheSourceBytesAndMtimeUnchangedAndCreatesNoSidecar()
    {
        string path = CreateSource(directory.Path, 2, SqliteFixtureJournal.Rollback);
        byte[] hashBefore = SHA256.HashData(File.ReadAllBytes(path));
        DateTime mtimeBefore = File.GetLastWriteTimeUtc(path);

        using (IHarnessSqliteDatabase database = Source().Open(path))
        {
            Assert.Equal(2, database.Query(new HarnessSqliteQuery("all", "SELECT id FROM messages", [])).Count());
            Assert.NotEmpty(database.GetColumns("messages"));
        }

        Assert.Equal(hashBefore, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["source.db"], Directory.GetFiles(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Open_ReadOnlyDirectoryWithAnEmptyWal_ReadsInPlaceThroughImmutable()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 2, SqliteFixtureJournal.WalEmpty);
        SkipUnlessSealed(sourceDirectory);
        AssertPlainOpenFails(path);

        using (IHarnessSqliteDatabase database = Source().Open(path))
        {
            Assert.Equal([1L, 2L], database.Query(new HarnessSqliteQuery("ids", "SELECT id FROM messages ORDER BY id", [])).Select(row => row["id"]));
        }

        Assert.Empty(CacheFiles());
        Assert.Equal(["source.db", "source.db-wal"], Directory.GetFiles(sourceDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Open_ReadOnlyDirectoryWithWalRows_ReadsThroughACopyHoldingTheWalRows()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        AssertPlainOpenFails(path);

        using (IHarnessSqliteDatabase database = Source().Open(path))
        {
            Assert.Equal([1L, 2L, 3L], database.Query(new HarnessSqliteQuery("ids", "SELECT id FROM messages ORDER BY id", [])).Select(row => row["id"]));
        }

        string copy = Assert.Single(CacheCopies());
        Assert.Matches("^[0-9a-f]{32}\\.[0-9a-f]{16}\\.db$", Path.GetFileName(copy));
        Assert.True(new FileInfo(copy + "-wal").Length > 0);
    }

    [Fact]
    public void Open_UnchangedSourceReusesItsCopyAndAChangedSourceReplacesIt()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 2, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        MicrosoftDataSqliteHarnessSource source = Source();

        using (source.Open(path))
        {
        }

        string firstCopy = Assert.Single(CacheCopies());
        DateTime firstCopyWritten = File.GetLastWriteTimeUtc(firstCopy);
        using (source.Open(path))
        {
        }

        Assert.Equal([firstCopy], CacheCopies());
        Assert.Equal(firstCopyWritten, File.GetLastWriteTimeUtc(firstCopy));

        Unseal(sourceDirectory);
        CreateSource(sourceDirectory, 5, SqliteFixtureJournal.WalUncheckpointed);
        Assert.False(File.Exists(path + "-shm"));
        SkipUnlessSealed(sourceDirectory);
        using (IHarnessSqliteDatabase database = source.Open(path))
        {
            Assert.Equal(5, database.Query(new HarnessSqliteQuery("ids", "SELECT id FROM messages", [])).Count());
        }

        string secondCopy = Assert.Single(CacheCopies());
        Assert.NotEqual(firstCopy, secondCopy);
        Assert.False(File.Exists(firstCopy));
        Assert.False(File.Exists(firstCopy + "-wal"));
    }

    [Fact]
    public void Open_CopyLargerThanTheCopyCap_IsRefusedWithTheCopyCapCode()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 2, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => Source(new HarnessSqliteLimits(CopyCapBytes: 1)).Open(path));

        Assert.StartsWith(SqliteSourceOpenPolicy.CopyCapExceededCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public void Open_MissingFile_IsRefusedAsUnreadable()
    {
        string path = directory.Combine("absent.db");

        IOException exception = Assert.Throws<IOException>(() => Source().Open(path));

        Assert.StartsWith(SqliteSourceOpenPolicy.SourceUnreadableCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Query_StatementOverTheCommandTimeout_ThrowsIOExceptionNamingTheTimeout()
    {
        string path = CreateSource(directory.Path, 1, SqliteFixtureJournal.Rollback);
        using IHarnessSqliteDatabase database = Source(new HarnessSqliteLimits(CommandTimeoutSeconds: 1)).Open(path);

        IOException exception = Assert.Throws<IOException>(() => database.Query(new HarnessSqliteQuery(
            "endless",
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT count(*) AS total FROM n",
            [])).ToList());

        Assert.Contains("command timeout of 1 s", exception.Message, StringComparison.Ordinal);
    }

    private MicrosoftDataSqliteHarnessSource Source(HarnessSqliteLimits? limits = null) =>
        new(limits ?? new HarnessSqliteLimits(), directory.Combine("cache"));

    private IEnumerable<string> CacheFiles() =>
        Directory.Exists(directory.Combine("cache")) ? Directory.GetFiles(directory.Combine("cache")) : [];

    private List<string> CacheCopies() =>
        [.. CacheFiles().Where(file => file.EndsWith(".db", StringComparison.Ordinal))];

    private static string CreateSource(string parent, int rowCount, SqliteFixtureJournal journal)
    {
        List<SqliteFixtureStatement> rows = [];
        for (int id = 1; id <= rowCount; id++)
        {
            rows.Add(new SqliteFixtureStatement(
                "INSERT INTO messages (id, role, body) VALUES (?1, ?2, ?3);",
                [(long)id, id % 2 == 0 ? "assistant" : "user", id == 3 ? null : $"message {id}"]));
        }

        return SqliteFixtureDatabase.Create(Path.Combine(parent, "source.db"), Ddl, rows, journal);
    }

    /// <summary>
    /// Makes <paramref name="path"/> read-only, or skips the test where that cannot be proved: Windows has
    /// no POSIX directory mode, and a superuser writes through one.
    /// </summary>
    private void SkipUnlessSealed(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Read-only-directory fallbacks need a POSIX directory mode, which Windows does not have.");
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        sealedDirectories.Add(path);
        string probe = Path.Combine(path, ".write-probe");
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        File.Delete(probe);
        Assert.Skip("The test user can write into a read-only directory (a superuser), so the fallback cannot be exercised.");
    }

    private static void Unseal(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Proves the fixture really needs the fallback, so the test cannot pass on a plain open.</summary>
    private static void AssertPlainOpenFails(string path)
    {
        SqliteProvider.EnsureInitialized();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        Assert.Throws<SqliteException>(() =>
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master";
            command.ExecuteScalar();
        });
    }
}
