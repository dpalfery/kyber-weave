using System.Security.Cryptography;
using KyberDash.Core.Repositories;
using KyberDash.Data.Collector.Sources;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Proves <see cref="MicrosoftDataSqliteHarnessSource"/> against real SQLite files: streaming, column
/// discovery, leaving the source untouched, the immutable and cache-copy fallbacks in a read-only
/// directory, copy reuse and replacement, the refusal codes, URI escaping of the immutable path, and the
/// busy and permission-denied classifications.
/// </summary>
public sealed class MicrosoftDataSqliteHarnessSourceSqliteTests : IDisposable
{
    private const string Ddl = "CREATE TABLE messages (id INTEGER PRIMARY KEY, role TEXT NOT NULL, body TEXT);";

    private readonly TempDirectory directory = new();
    private readonly List<string> sealedDirectories = [];
    private readonly List<string> unreadableFiles = [];

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Restores the permissions the tests changed and deletes the scratch directory when disposing.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (string unreadableFile in unreadableFiles)
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(unreadableFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            foreach (string sealedDirectory in sealedDirectories)
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(sealedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            directory.Dispose();
        }
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

    [Theory]
    [InlineData("SELECT ? AS cell", "text")]
    [InlineData("SELECT CAST(? AS BLOB) AS cell", "blob")]
    [InlineData("SELECT zeroblob(17) AS cell", "zeroblob")]
    public void Query_CellLargerThanTheExportCap_IsRefusedWithTheExportCapCode(string sql, string kind)
    {
        string path = CreateSource(directory.Path, 1, SqliteFixtureJournal.Rollback);
        using IHarnessSqliteDatabase database = Source(new HarnessSqliteLimits(ExportCapBytes: 16)).Open(path);
        object?[] parameters = kind == "zeroblob" ? [] : [new string('x', 17)];

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => database.Query(new HarnessSqliteQuery("big-cell", sql, parameters)).ToList());

        Assert.StartsWith("SQLITE_EXPORT_CAP_EXCEEDED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Query_CellExactlyAtTheExportCap_IsRead()
    {
        string path = CreateSource(directory.Path, 1, SqliteFixtureJournal.Rollback);
        using IHarnessSqliteDatabase database = Source(new HarnessSqliteLimits(ExportCapBytes: 16)).Open(path);

        IReadOnlyDictionary<string, object?> row = Assert.Single(
            database.Query(new HarnessSqliteQuery("cap-cell", "SELECT ? AS cell, 12345678901234567 AS number", [new string('x', 16)])));

        Assert.Equal(new string('x', 16), row["cell"]);
        Assert.Equal(12345678901234567L, row["number"]);
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
    public void Open_ReadOnlyDirectoryWhoseNameHoldsUriSpecialCharacters_ReadsInPlaceThroughImmutable()
    {
        // '#' would start a URI fragment, '?' the query and "%41" would decode to "A" if the immutable
        // URI were not escaped, so SQLite would open some other path.
        string sourceDirectory = directory.Combine("har#ness?dir%41");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 2, SqliteFixtureJournal.WalEmpty);
        SkipUnlessSealed(sourceDirectory);
        AssertPlainOpenFails(path);

        using (IHarnessSqliteDatabase database = Source().Open(path))
        {
            Assert.Equal([1L, 2L], database.Query(new HarnessSqliteQuery("ids", "SELECT id FROM messages ORDER BY id", [])).Select(row => row["id"]));
        }

        Assert.Empty(CacheFiles());
    }

    [Fact]
    public void Open_DatabaseUnderAnExclusiveWriteLock_IsClassifiedBusyAndReadThroughTheFallback()
    {
        string path = CreateSource(directory.Path, 2, SqliteFixtureJournal.Rollback);
        using SqliteConnection writer = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        writer.Open();
        using (SqliteCommand begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE";
            begin.ExecuteNonQuery();
        }

        SqliteException plain = Assert.Throws<SqliteException>(() => PlainOpen(path));
        Assert.Equal(5, plain.SqliteErrorCode & 0xff);

        using (IHarnessSqliteDatabase database = Source(new HarnessSqliteLimits(CommandTimeoutSeconds: 1, BusyTimeoutMilliseconds: 0)).Open(path))
        {
            Assert.Equal([1L, 2L], database.Query(new HarnessSqliteQuery("ids", "SELECT id FROM messages ORDER BY id", [])).Select(row => row["id"]));
        }
    }

    [Fact]
    public void Open_SourceFileTheProcessCannotRead_IsRefusedAsUnreadableWithoutACopy()
    {
        string path = CreateSource(directory.Path, 2, SqliteFixtureJournal.Rollback);
        SkipUnlessUnreadable(path);

        IOException exception = Assert.Throws<IOException>(() => Source().Open(path));

        Assert.StartsWith(SqliteSourceOpenPolicy.SourceUnreadableCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        // Refused at classification: neither the immutable nor the copy fallback was tried.
        Assert.DoesNotContain("immutable", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(directory.Combine("cache")));
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
    public void Open_CacheRootIsAFile_ThrowsSourceUnreadableNamingTheSource()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        File.WriteAllText(directory.Combine("cache"), "not a directory");

        IOException exception = Assert.Throws<IOException>(() => Source().Open(path));

        Assert.StartsWith(SqliteSourceOpenPolicy.SourceUnreadableCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
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
    public void Open_WalThatFillsAfterTheSizeCheck_IsReadThroughACopyRatherThanHiddenByImmutable()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        AssertPlainOpenFails(path);
        // The first WAL size check sees an empty WAL, as it would if a writer filled the WAL just after it.
        bool walReportedEmpty = false;
        long StaleWalLength(string file)
        {
            if (!walReportedEmpty && file.EndsWith("-wal", StringComparison.Ordinal))
            {
                walReportedEmpty = true;
                return 0;
            }

            return File.Exists(file) ? new FileInfo(file).Length : 0;
        }

        using (IHarnessSqliteDatabase database = new MicrosoftDataSqliteHarnessSource(new HarnessSqliteLimits(), directory.Combine("cache"), fileLength: StaleWalLength).Open(path))
        {
            Assert.Equal([1L, 2L, 3L], database.Query(new HarnessSqliteQuery("ids", "SELECT id FROM messages ORDER BY id", [])).Select(row => row["id"]));
        }

        Assert.True(walReportedEmpty);
        Assert.Single(CacheCopies());
    }

    [Fact]
    public void Open_WalRecheckAfterTheImmutableOpen_GoesThroughTheLengthProbe()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        AssertPlainOpenFails(path);
        // The probe reports an empty WAL every time; the recheck must ask the probe, not the file system,
        // so a denied or vanished WAL surfaces through the probe's error mapping.
        int walChecks = 0;
        long EmptyWal(string file)
        {
            if (file.EndsWith("-wal", StringComparison.Ordinal))
            {
                walChecks++;
                return 0;
            }

            return new FileInfo(file).Length;
        }

        MicrosoftDataSqliteHarnessSource source = new(new HarnessSqliteLimits(), directory.Combine("cache"), fileLength: EmptyWal);

        using (source.Open(path))
        {
        }

        Assert.Equal(2, walChecks);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public void Open_LengthProbeThatFailsWithAnIOError_IsReportedAsUnreadable()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        AssertPlainOpenFails(path);
        MicrosoftDataSqliteHarnessSource source = new(
            new HarnessSqliteLimits(),
            directory.Combine("cache"),
            fileLength: static file => throw new FileNotFoundException("gone", file));

        IOException exception = Assert.Throws<IOException>(() => source.Open(path));

        Assert.StartsWith(SqliteSourceOpenPolicy.SourceUnreadableCode, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_SourceThatGrewPastTheCopyCapAfterTheSizeCheck_IsRefusedWithTheCopyCapCode()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        // The size check sees one byte per file, as it would before a writer grew them; the real files exceed the cap.
        MicrosoftDataSqliteHarnessSource source = new(new HarnessSqliteLimits(CopyCapBytes: 100), directory.Combine("cache"), fileLength: _ => 1);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => source.Open(path));

        Assert.StartsWith(SqliteSourceOpenPolicy.CopyCapExceededCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Empty(CacheFiles());
    }

    [Fact]
    public void CopyWithinCap_SourceLargerThanTheBudget_StopsWithTheCopyCapCode()
    {
        string sourceFile = directory.Combine("grown.bin");
        File.WriteAllBytes(sourceFile, new byte[2_000]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => MicrosoftDataSqliteHarnessSource.CopyWithinCap(sourceFile, directory.Combine("copy.bin"), 1_000, 1_000));

        Assert.StartsWith(SqliteSourceOpenPolicy.CopyCapExceededCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains(sourceFile, exception.Message, StringComparison.Ordinal);
        Assert.True(new FileInfo(directory.Combine("copy.bin")).Length <= 1_000);
    }

    [Fact]
    public void CopyWithinCap_SourceExactlyTheBudget_CopiesEveryByte()
    {
        string sourceFile = directory.Combine("exact.bin");
        byte[] content = RandomNumberGenerator.GetBytes(1_000);
        File.WriteAllBytes(sourceFile, content);

        long copied = MicrosoftDataSqliteHarnessSource.CopyWithinCap(sourceFile, directory.Combine("copy.bin"), 1_000, 1_000);

        Assert.Equal(1_000, copied);
        Assert.Equal(content, File.ReadAllBytes(directory.Combine("copy.bin")));
    }

    [Fact]
    public void Open_CacheCopyOfAWorldReadableSource_IsReadableByTheOwnerOnly()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode worldReadable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            File.SetUnixFileMode(path, worldReadable);
            File.SetUnixFileMode(path + "-wal", worldReadable);
        }

        SkipUnlessSealed(sourceDirectory);

        using (Source().Open(path))
        {
        }

        string copy = Assert.Single(CacheCopies());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(copy));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(copy + "-wal"));
        }
    }

    [Fact]
    public void Open_LeftoverTempFilesUnderTheOldSequentialNames_DoNotBlockTheCopy()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        using (Source().Open(path))
        {
        }

        string copy = Assert.Single(CacheCopies());
        File.Delete(copy);
        File.Delete(copy + "-wal");
        // A crashed run, or another process under a reused process id, left temps at every name the old
        // pid-and-sequence scheme would pick next.
        for (int sequence = 1; sequence <= 2_000; sequence++)
        {
            File.WriteAllBytes($"{copy}.tmp-{Environment.ProcessId}-{sequence}", []);
        }

        using (IHarnessSqliteDatabase database = Source().Open(path))
        {
            Assert.Equal(3, database.Query(new HarnessSqliteQuery("ids", "SELECT id FROM messages", [])).Count());
        }

        Assert.True(File.Exists(copy));
    }

    [Fact]
    public void Open_StaleTempFilesInTheCache_AreDeletedAndARecentOneIsKept()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        Directory.CreateDirectory(directory.Combine("cache"));
        string staleOwn = Path.Combine(directory.Combine("cache"), "0123456789abcdef0123456789abcdef.0123456789abcdef.db.tmp-1-1");
        string staleWal = staleOwn + "-wal";
        string staleOther = Path.Combine(directory.Combine("cache"), "fedcba9876543210fedcba9876543210.fedcba9876543210.db.tmp-0a1b2c");
        string recent = Path.Combine(directory.Combine("cache"), "fedcba9876543210fedcba9876543210.fedcba9876543210.db.tmp-3d4e5f");
        foreach (string temp in new[] { staleOwn, staleWal, staleOther, recent })
        {
            File.WriteAllBytes(temp, [1, 2, 3]);
        }

        foreach (string temp in new[] { staleOwn, staleWal, staleOther })
        {
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddDays(-1));
        }

        using (Source().Open(path))
        {
        }

        Assert.False(File.Exists(staleOwn));
        Assert.False(File.Exists(staleWal));
        Assert.False(File.Exists(staleOther));
        // A recent temp may be another process's copy in progress.
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Open_SchemaProbeOverTheCommandTimeout_ThrowsIOExceptionNamingTheTimeout()
    {
        // A wide schema gives the probe enough SQLite steps for the progress handler to run.
        string ddl = string.Concat(Enumerable.Range(1, 2_000).Select(table => $"CREATE TABLE t{table} (id INTEGER PRIMARY KEY, body TEXT);"));
        string path = SqliteFixtureDatabase.Create(directory.Combine("wide.db"), ddl, [], SqliteFixtureJournal.Rollback);
        // The clock reads zero when the deadline is armed and has passed it by every later reading.
        long readings = 0;
        long Clock() => Interlocked.Increment(ref readings) == 1 ? 0 : long.MaxValue / 2;
        MicrosoftDataSqliteHarnessSource source = new(new HarnessSqliteLimits(CommandTimeoutSeconds: 1), directory.Combine("cache"), timestamp: Clock);

        IOException exception = Assert.Throws<IOException>(() => source.Open(path));

        Assert.StartsWith(SqliteSourceOpenPolicy.SourceUnreadableCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains("command timeout of 1 s", exception.Message, StringComparison.Ordinal);
        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_WalRewrittenAtTheSameSizeAndTimeBetweenTheCopies_DiscardsTheCopy()
    {
        string sourceDirectory = directory.Combine("harness");
        Directory.CreateDirectory(sourceDirectory);
        string path = CreateSource(sourceDirectory, 3, SqliteFixtureJournal.WalUncheckpointed);
        SkipUnlessSealed(sourceDirectory);
        // A checkpoint and a WAL restart inside one timestamp tick of a coarse file system: the WAL keeps its
        // size and last-write time, and only its salts (header offsets 16 to 23) change.
        void RestartWalInOneTick(string source)
        {
            string wal = source + "-wal";
            DateTime written = File.GetLastWriteTimeUtc(wal);
            using (FileStream stream = new(wal, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                byte[] salts = new byte[8];
                stream.Position = 16;
                stream.ReadExactly(salts);
                for (int i = 0; i < salts.Length; i++)
                {
                    salts[i] ^= 0xff;
                }

                stream.Position = 16;
                stream.Write(salts);
            }

            File.SetLastWriteTimeUtc(wal, written);
        }

        MicrosoftDataSqliteHarnessSource source = new(new HarnessSqliteLimits(), directory.Combine("cache"), betweenCopies: RestartWalInOneTick);

        IOException exception = Assert.Throws<IOException>(() => source.Open(path));

        Assert.StartsWith(SqliteSourceOpenPolicy.SourceUnreadableCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains("changed while copying", exception.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// Removes every permission from <paramref name="path"/>, or skips the test where that cannot be proved:
    /// Windows has no POSIX file mode, and a superuser reads through one.
    /// </summary>
    private void SkipUnlessUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("An unreadable source needs a POSIX file mode, which Windows does not have.");
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        unreadableFiles.Add(path);
        try
        {
            File.ReadAllBytes(path);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.Skip("The test user can read a file with no permissions (a superuser), so the refusal cannot be exercised.");
    }

    /// <summary>Proves the fixture really needs the fallback, so the test cannot pass on a plain open.</summary>
    private static void AssertPlainOpenFails(string path) =>
        Assert.Throws<SqliteException>(() => PlainOpen(path));

    /// <summary>Opens <paramref name="path"/> plainly read-only and reads its schema; throws when SQLite refuses.</summary>
    private static void PlainOpen(string path)
    {
        SqliteProvider.EnsureInitialized();
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString())
        {
            // Microsoft.Data.Sqlite retries SQLITE_BUSY for the whole command timeout; one second keeps the
            // busy probe short.
            DefaultTimeout = 1,
        };
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master";
        command.ExecuteScalar();
    }
}
