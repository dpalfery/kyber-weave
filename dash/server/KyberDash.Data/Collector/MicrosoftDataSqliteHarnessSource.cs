using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberDash.Core.Repositories;
using KyberDash.Domain;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace KyberDash.Data.Collector.Sources;

/// <summary>
/// Opens harness-owned SQLite databases read-only through <c>Microsoft.Data.Sqlite</c>, executing
/// <see cref="SqliteSourceOpenPolicy"/> when the plain open fails: an in-place <c>immutable=1</c> open when
/// the WAL is empty, otherwise a fingerprinted copy under the cache root.
/// </summary>
/// <remarks>
/// <para>
/// It reads through the system SQLite registered by <c>SQLitePCLRaw.provider.sqlite3</c>
/// (<see cref="SqliteProvider"/>): no bundled native binary ships with KyberDash, and no <c>sqlite3</c>
/// process is started per query, which the C# standard forbids for runtime database access.
/// </para>
/// <para>
/// The fallback order mirrors the deleted TypeScript reader (<c>git show 'f4fbf7db3^:dash/archived/src/ingest/sqlite.ts'</c>,
/// <c>openDatabase</c> and <c>openReadonlyCache</c>). A harness may keep its database in a directory this
/// user cannot write, where SQLite cannot create the <c>-shm</c> a WAL reader needs. An empty WAL holds no
/// committed rows, so <c>immutable=1</c> reads the file in place; a non-empty WAL must be read, so the
/// database and WAL are copied. The copy is fingerprinted before and after so a database and a WAL from
/// different moments are never paired; the WAL is published before the database so a reader never sees the
/// database without it; and the copy is named after the source and its fingerprint, so a changed source gets
/// a new file and a reader of the old copy is never overwritten.
/// </para>
/// <para>
/// <c>SqliteCommand.CommandTimeout</c> only bounds busy retries, so the per-command timeout is enforced by a
/// progress handler that interrupts a statement once it has spent the limit inside SQLite.
/// </para>
/// </remarks>
public class MicrosoftDataSqliteHarnessSource : IHarnessSqliteSource
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteReadOnly = 8;
    private const int SqliteInterrupt = 9;
    private const int SqliteCantOpen = 14;
    private const int SqliteAuth = 23;

    private const int MaxOpenAttempts = 3;

    /// <summary>A temp file not written for this long is left over from a run that stopped; younger ones may be in progress.</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    private readonly HarnessSqliteLimits limits;
    private readonly string cacheRoot;
    private readonly Func<string, long> fileLength;
    private readonly Func<long> timestamp;
    private readonly Action<string>? betweenCopies;

    /// <summary>Creates the adapter.</summary>
    /// <param name="limits">Size and time limits.</param>
    /// <param name="cacheRoot">Directory that holds the read-only copies; created on first use.</param>
    public MicrosoftDataSqliteHarnessSource(HarnessSqliteLimits limits, string cacheRoot)
        : this(limits, cacheRoot, null, null)
    {
    }

    /// <summary>Creates the adapter with the probes tests use to report a stale size or a late clock.</summary>
    /// <param name="limits">Size and time limits.</param>
    /// <param name="cacheRoot">Directory that holds the read-only copies; created on first use.</param>
    /// <param name="fileLength">Length of a file, zero when it is absent, read for the open decision; the file system when null.</param>
    /// <param name="timestamp">The clock the command timeout is measured on, in <see cref="Stopwatch"/> ticks; <see cref="Stopwatch.GetTimestamp"/> when null.</param>
    /// <param name="betweenCopies">Runs with the source path after the database is copied and before its WAL is; tests use it to change the source mid-copy.</param>
    internal MicrosoftDataSqliteHarnessSource(
        HarnessSqliteLimits limits,
        string cacheRoot,
        Func<string, long>? fileLength = null,
        Func<long>? timestamp = null,
        Action<string>? betweenCopies = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limits.CommandTimeoutSeconds, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.BusyTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(limits.CopyCapBytes);
        this.limits = limits;
        this.cacheRoot = Path.GetFullPath(cacheRoot);
        this.fileLength = fileLength ?? LengthOrZero;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        this.betweenCopies = betweenCopies;
    }

    /// <summary>Creates the adapter with default limits and the cache root <c>&lt;canon.db directory&gt;/sqlite-ro</c>.</summary>
    public static MicrosoftDataSqliteHarnessSource CreateDefault()
    {
        string canon = Path.GetFullPath(CanonDbPath.Resolve());
        string directory = Path.GetDirectoryName(canon)
            ?? throw new InvalidOperationException($"The canonical store path '{canon}' has no parent directory.");
        return new MicrosoftDataSqliteHarnessSource(new HarnessSqliteLimits(), Path.Combine(directory, "sqlite-ro"));
    }

    /// <inheritdoc />
    /// <exception cref="InvalidDataException">The copy fallback would copy more than the copy cap.</exception>
    public IHarnessSqliteDatabase Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SqliteProvider.EnsureInitialized();
        string path = Path.GetFullPath(databasePath);

        SqliteOpenFailure failure;
        string? cause;
        if (!File.Exists(path))
        {
            failure = SqliteOpenFailure.Missing;
            cause = "the file does not exist";
        }
        else
        {
            try
            {
                return Connect(PlainConnectionString(path), path);
            }
            catch (SqliteException ex)
            {
                failure = Classify(ex, path)
                    ?? throw Unreadable(path, $"SQLite error {ex.SqliteErrorCode}: {ex.Message}", ex);
                cause = $"SQLite error {ex.SqliteErrorCode}: {ex.Message}";
            }
        }

        // An immutable open ignores the WAL, so a WAL that fills between the size check and the open would
        // hide committed rows; the WAL is checked again once the immutable connection is open, and the
        // decision is taken again on the new sizes.
        for (int attempt = 1; ; attempt++)
        {
            long walBytes;
            long databaseBytes;
            try
            {
                walBytes = fileLength(path + "-wal");
                databaseBytes = fileLength(path);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw Unreadable(path, "permission denied", ex);
            }

            SqliteOpenAction action = SqliteSourceOpenPolicy.Decide(failure, walBytes, databaseBytes, limits.CopyCapBytes);
            switch (action.Kind)
            {
                case SqliteOpenActionKind.Immutable:
                    OpenDatabase immutable;
                    try
                    {
                        immutable = Connect(ImmutableConnectionString(path), path);
                    }
                    catch (SqliteException ex)
                    {
                        throw Unreadable(path, $"the immutable open failed with SQLite error {ex.SqliteErrorCode}: {ex.Message}", ex);
                    }

                    if (LengthOrZero(path + "-wal") <= 0)
                    {
                        return immutable;
                    }

                    immutable.Dispose();
                    if (attempt >= MaxOpenAttempts)
                    {
                        throw Unreadable(path, $"its WAL was still being written after {MaxOpenAttempts} attempts to open it", null);
                    }

                    continue;

                case SqliteOpenActionKind.CacheCopy:
                    string copy = CopyToCache(path);
                    try
                    {
                        return Connect(PlainConnectionString(copy), path);
                    }
                    catch (SqliteException ex)
                    {
                        throw Unreadable(path, $"its cache copy '{copy}' failed with SQLite error {ex.SqliteErrorCode}: {ex.Message}", ex);
                    }

                case SqliteOpenActionKind.Refuse when action.ProblemCode == SqliteSourceOpenPolicy.CopyCapExceededCode:
                    throw CopyCapExceeded(path, databaseBytes, walBytes, limits.CopyCapBytes);

                case SqliteOpenActionKind.Refuse:
                    throw new IOException($"{action.ProblemCode}: cannot open '{path}': {cause}.");

                default:
                    throw Unreadable(path, $"the open policy chose {action.Kind} after a {failure} failure", null);
            }
        }
    }

    private static InvalidDataException CopyCapExceeded(string path, long databaseBytes, long walBytes, long copyCapBytes) =>
        new($"{SqliteSourceOpenPolicy.CopyCapExceededCode}: '{path}' needs a cache copy of {databaseBytes} database bytes " +
            $"and {walBytes} WAL bytes, more than the copy cap of {copyCapBytes} bytes.");

    private static string PlainConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();

    /// <summary>
    /// The URI form SQLite needs for <c>immutable=1</c>. Only <c>%</c>, <c>?</c> and <c>#</c> are special in a
    /// SQLite URI path, so they are the characters escaped.
    /// </summary>
    private static string ImmutableConnectionString(string path)
    {
        string uriPath = path.Replace('\\', '/');
        StringBuilder escaped = new("file:");
        if (!uriPath.StartsWith('/'))
        {
            escaped.Append('/');
        }

        foreach (char character in uriPath)
        {
            _ = character switch
            {
                '%' => escaped.Append("%25"),
                '?' => escaped.Append("%3F"),
                '#' => escaped.Append("%23"),
                _ => escaped.Append(character),
            };
        }

        escaped.Append("?immutable=1");
        return new SqliteConnectionStringBuilder
        {
            DataSource = escaped.ToString(),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
    }

    /// <summary>
    /// Opens and probes a connection. The probe reads the schema because SQLite opens lazily: a missing
    /// sidecar only fails on the first read, and it must fail here, where the fallback can still run.
    /// </summary>
    private OpenDatabase Connect(string connectionString, string sourcePath)
    {
        SqliteConnection connection = new(connectionString) { DefaultTimeout = limits.CommandTimeoutSeconds };
        bool handedOver = false;
        try
        {
            connection.Open();
            // The same setting as PRAGMA busy_timeout, set through the API so no SQL is assembled.
            int busyResult = raw.sqlite3_busy_timeout(connection.Handle, limits.BusyTimeoutMilliseconds);
            if (busyResult != raw.SQLITE_OK)
            {
                SqliteException.ThrowExceptionForRC(busyResult, connection.Handle);
            }

            OpenDatabase database = new(connection, sourcePath, limits.CommandTimeoutSeconds, limits.ExportCapBytes, timestamp);
            database.ProbeSchema();
            handedOver = true;
            return database;
        }
        finally
        {
            if (!handedOver)
            {
                connection.Dispose();
            }
        }
    }

    private static SqliteOpenFailure? Classify(SqliteException exception, string path)
    {
        int primary = exception.SqliteErrorCode & 0xff;
        switch (primary)
        {
            case SqliteReadOnly:
                return SqliteOpenFailure.ReadOnlyDirectory;
            case SqliteBusy:
            case SqliteLocked:
                return SqliteOpenFailure.Busy;
            case SqliteAuth:
                return SqliteOpenFailure.PermissionDenied;
            case SqliteCantOpen when !File.Exists(path):
                return SqliteOpenFailure.Missing;
            case SqliteCantOpen when File.Exists(path + "-wal"):
                return SqliteOpenFailure.MissingSidecar;
            case SqliteCantOpen:
                // A readable file with no WAL that still cannot be opened lacks a sidecar SQLite could not
                // create; an unreadable one is a permission denial the copy would hit too.
                return CanRead(path) ? SqliteOpenFailure.MissingSidecar : SqliteOpenFailure.PermissionDenied;
            default:
                return exception.Message.Contains("authorization denied", StringComparison.OrdinalIgnoreCase)
                    ? SqliteOpenFailure.PermissionDenied
                    : null;
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static long LengthOrZero(string path)
    {
        FileInfo file = new(path);
        return file.Exists ? file.Length : 0;
    }

    private static IOException Unreadable(string path, string reason, Exception? inner) =>
        new($"{SqliteSourceOpenPolicy.SourceUnreadableCode}: cannot open '{path}': {reason}.", inner);

    /// <summary>Copies the database and its WAL into the cache, or reuses the copy of this exact fingerprint.</summary>
    private string CopyToCache(string sourcePath)
    {
        try
        {
            EnsureCacheRoot();
            EvictStaleTemps();
            string sourceKey = Hex(sourcePath)[..32];
            SourceFingerprint before = SourceFingerprint.Of(sourcePath);
            long walBytes = Math.Max(before.WalBytes, 0);
            if (before.DatabaseBytes > limits.CopyCapBytes || walBytes > limits.CopyCapBytes - before.DatabaseBytes)
            {
                // The open decision read the sizes earlier; the source may have grown since.
                throw CopyCapExceeded(sourcePath, before.DatabaseBytes, walBytes, limits.CopyCapBytes);
            }

            string name = $"{sourceKey}.{Hex(before.ToString())[..16]}.db";
            string cachePath = Path.Combine(cacheRoot, name);
            if (!File.Exists(cachePath))
            {
                Publish(sourcePath, cachePath, before, limits.CopyCapBytes, betweenCopies);
            }

            EvictSuperseded(sourceKey, name);
            return cachePath;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Unreadable(sourcePath, "permission denied while copying it to the cache", ex);
        }
        catch (IOException ex) when (!ex.Message.StartsWith(SqliteSourceOpenPolicy.SourceUnreadableCode, StringComparison.Ordinal))
        {
            throw Unreadable(sourcePath, $"its cache copy could not be written: {ex.Message}", ex);
        }
    }

    private void EnsureCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(cacheRoot);
        }
        else
        {
            Directory.CreateDirectory(cacheRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void Publish(string sourcePath, string cachePath, SourceFingerprint before, long copyCapBytes, Action<string>? betweenCopies)
    {
        // A random name: a temp left by a stopped run, or by a process that reused this process id, can never
        // be the name this copy needs.
        string tempDatabase = $"{cachePath}.tmp-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
        string tempWal = tempDatabase + "-wal";
        try
        {
            // The cap is enforced on the bytes actually read, since the source can grow while it is copied.
            long copied = CopyWithinCap(sourcePath, tempDatabase, copyCapBytes, copyCapBytes);
            betweenCopies?.Invoke(sourcePath);
            bool hasWal = File.Exists(sourcePath + "-wal");
            if (hasWal)
            {
                CopyWithinCap(sourcePath + "-wal", tempWal, copyCapBytes - copied, copyCapBytes);
            }

            if (SourceFingerprint.Of(sourcePath) != before)
            {
                throw Unreadable(sourcePath, "it changed while copying it to the cache; the copy was discarded", null);
            }

            // The WAL goes first: a reader that can see the database copy must never find it without the WAL
            // that holds its most recent rows.
            if (hasWal)
            {
                MoveUnlessPublished(tempWal, cachePath + "-wal");
            }

            MoveUnlessPublished(tempDatabase, cachePath);
        }
        finally
        {
            File.Delete(tempDatabase);
            File.Delete(tempWal);
        }
    }

    /// <summary>
    /// Publishes by rename. A concurrent reader may have published the same name first; the name is the
    /// fingerprint, so its content is identical and losing that race is not an error.
    /// </summary>
    private static void MoveUnlessPublished(string temporary, string destination)
    {
        try
        {
            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
        }
    }

    /// <summary>
    /// Deletes copies of the same source taken at other fingerprints. A copy still held open (Windows) cannot
    /// be deleted; it stays a complete, stale copy and the next publish for this source deletes it.
    /// </summary>
    private void EvictSuperseded(string sourceKey, string keepName)
    {
        foreach (string file in Directory.EnumerateFiles(cacheRoot, sourceKey + ".*.db"))
        {
            string fileName = Path.GetFileName(file);
            if (string.Equals(fileName, keepName, StringComparison.Ordinal) || !fileName.EndsWith(".db", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                File.Delete(file + "-wal");
                File.Delete(file + "-shm");
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("KyberDash: superseded SQLite cache copy '{0}' not deleted yet: {1}", file, ex.Message);
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="source"/> to a new <paramref name="destination"/>, reading at most
    /// <paramref name="budget"/> bytes, and returns the bytes copied.
    /// </summary>
    /// <exception cref="InvalidDataException">The source holds more than <paramref name="budget"/> bytes.</exception>
    internal static long CopyWithinCap(string source, string destination, long budget, long copyCapBytes)
    {
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        FileStreamOptions create = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            // Owner-only, whatever the source's mode or the umask: the copy holds the harness's conversations.
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using FileStream output = new(destination, create);
        byte[] buffer = new byte[81_920];
        long copied = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read > budget - copied)
            {
                throw new InvalidDataException(
                    $"{SqliteSourceOpenPolicy.CopyCapExceededCode}: '{source}' grew past the copy cap of {copyCapBytes} bytes while it was copied.");
            }

            output.Write(buffer, 0, read);
            copied += read;
        }

        return copied;
    }

    /// <summary>
    /// Deletes temp copies, of any source, that have not been written for <see cref="StaleTempAge"/>: a run
    /// that stopped mid-copy leaves them, and no publish ever renames them.
    /// </summary>
    private void EvictStaleTemps()
    {
        DateTime staleBefore = DateTime.UtcNow - StaleTempAge;
        foreach (string file in Directory.EnumerateFiles(cacheRoot, "*.db.tmp-*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < staleBefore)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("KyberDash: stale SQLite cache temp '{0}' not deleted yet: {1}", file, ex.Message);
            }
        }
    }

    private static string Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Size and last-write time of a database and its WAL, and the WAL header's salts; a WAL that is absent
    /// records -1, and a WAL too short to hold its salts records -1 for them.
    /// </summary>
    /// <remarks>
    /// SQLite writes new salts into the WAL header each time it restarts the WAL after a checkpoint. On a file
    /// system whose timestamps are coarse, a checkpoint and a same-size WAL rewrite inside one tick leave the
    /// size and time unchanged; the salts still differ, so the copy is still discarded.
    /// </remarks>
    private readonly record struct SourceFingerprint(long DatabaseBytes, long DatabaseTicks, long WalBytes, long WalTicks, long WalSalts)
    {
        /// <summary>Offset of salt-1 in the 32-byte WAL header; salt-2 follows it at offset 20.</summary>
        private const int WalSaltsOffset = 16;

        public static SourceFingerprint Of(string path)
        {
            FileInfo database = new(path);
            FileInfo wal = new(path + "-wal");
            if (!database.Exists)
            {
                throw new FileNotFoundException($"{SqliteSourceOpenPolicy.SourceUnreadableCode}: '{path}' disappeared while copying it.", path);
            }

            return new SourceFingerprint(
                database.Length,
                database.LastWriteTimeUtc.Ticks,
                wal.Exists ? wal.Length : -1,
                wal.Exists ? wal.LastWriteTimeUtc.Ticks : -1,
                wal.Exists ? ReadWalSalts(wal.FullName) : -1);
        }

        private static long ReadWalSalts(string walPath)
        {
            try
            {
                using FileStream stream = new(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> salts = stackalloc byte[sizeof(long)];
                stream.Position = WalSaltsOffset;
                return stream.ReadAtLeast(salts, salts.Length, throwOnEndOfStream: false) == salts.Length
                    ? BinaryPrimitives.ReadInt64BigEndian(salts)
                    : -1;
            }
            catch (FileNotFoundException)
            {
                // Deleted since it was sized: the size and time recorded with it already differ from any later reading.
                return -1;
            }
        }

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"{DatabaseBytes}:{DatabaseTicks}:{WalBytes}:{WalTicks}:{WalSalts}");
    }

    /// <summary>One open read-only connection; queries stream through a forward-only reader.</summary>
    private sealed class OpenDatabase : IHarnessSqliteDatabase
    {
        private readonly SqliteConnection connection;
        private readonly string sourcePath;
        private readonly int commandTimeoutSeconds;
        private readonly long cellCapBytes;
        private readonly long timeoutTicks;
        private readonly Func<long> timestamp;
        private readonly delegate_progress progress;
        private long deadline = long.MaxValue;

        public OpenDatabase(SqliteConnection connection, string sourcePath, int commandTimeoutSeconds, long cellCapBytes, Func<long> timestamp)
        {
            this.connection = connection;
            this.sourcePath = sourcePath;
            this.commandTimeoutSeconds = commandTimeoutSeconds;
            this.cellCapBytes = cellCapBytes;
            this.timestamp = timestamp;
            timeoutTicks = commandTimeoutSeconds * Stopwatch.Frequency;
            progress = _ => timestamp() > Volatile.Read(ref deadline) ? 1 : 0;
            raw.sqlite3_progress_handler(connection.Handle, 1000, progress, null);
        }

        /// <summary>
        /// Reads the schema under the command timeout. Any other SQLite error is rethrown as it is, so the
        /// open can still classify it and fall back.
        /// </summary>
        public void ProbeSchema()
        {
            using SqliteCommand probe = connection.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master";
            Volatile.Write(ref deadline, timestamp() + timeoutTicks);
            try
            {
                probe.ExecuteScalar();
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xff) == SqliteInterrupt)
            {
                throw new IOException(
                    $"{SqliteSourceOpenPolicy.SourceUnreadableCode}: reading the schema of '{sourcePath}' exceeded the command timeout of {commandTimeoutSeconds} s.",
                    ex);
            }
            finally
            {
                Volatile.Write(ref deadline, long.MaxValue);
            }
        }

        public IEnumerable<IReadOnlyDictionary<string, object?>> Query(HarnessSqliteQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            string sql = NumberPositionalParameters(query.Sql, query.Parameters.Count, query.Name);
            return Stream(query, sql);
        }

        public IReadOnlySet<string> GetColumns(string table)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(table);
            HashSet<string> columns = new(StringComparer.Ordinal);
            foreach (IReadOnlyDictionary<string, object?> row in Stream(
                new HarnessSqliteQuery("columns", "SELECT name FROM pragma_table_info(?)", [table]),
                "SELECT name FROM pragma_table_info(?1)"))
            {
                if (row["name"] is string name)
                {
                    columns.Add(name);
                }
            }

            return columns;
        }

        public void Dispose() => connection.Dispose();

        // reason: the SQL is the reader's own constant text with its markers numbered; values are always bound.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Reader-owned constant SQL; every value is a bound parameter.")]
        private IEnumerable<IReadOnlyDictionary<string, object?>> Stream(HarnessSqliteQuery query, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql; // nosemgrep: csharp.lang.security.sqli.csharp-sqli.csharp-sqli
            command.CommandTimeout = commandTimeoutSeconds;
            for (int i = 0; i < query.Parameters.Count; i++)
            {
                command.Parameters.AddWithValue("?" + (i + 1).ToString(CultureInfo.InvariantCulture), query.Parameters[i] ?? DBNull.Value);
            }

            long remaining = timeoutTicks;
            SqliteDataReader reader = Step(query, ref remaining, command.ExecuteReader);
            using (reader)
            {
                while (Step(query, ref remaining, reader.Read))
                {
                    Dictionary<string, object?> row = new(reader.FieldCount, StringComparer.Ordinal);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        string name = reader.GetName(i);
                        if (!row.TryAdd(name, reader.IsDBNull(i) ? null : CellValue(query, reader, i, name)))
                        {
                            throw new InvalidDataException($"Query '{query.Name}' on '{sourcePath}' returns column '{name}' more than once.");
                        }
                    }

                    yield return row;
                }
            }
        }

        /// <summary>
        /// Reads one cell, sizing a TEXT or BLOB cell inside SQLite first, so a cell that alone is larger than
        /// the export cap is refused before any of it is copied into managed memory.
        /// </summary>
        /// <exception cref="InvalidDataException">The cell is larger than the export cap; the message carries the problem code.</exception>
        private object CellValue(HarnessSqliteQuery query, SqliteDataReader reader, int ordinal, string name)
        {
            // Only TEXT and BLOB are sized: sqlite3_column_bytes on a number converts it to text in place.
            int type = raw.sqlite3_column_type(reader.Handle, ordinal);
            if (type is raw.SQLITE_TEXT or raw.SQLITE_BLOB)
            {
                long bytes = raw.sqlite3_column_bytes(reader.Handle, ordinal);
                if (bytes > cellCapBytes)
                {
                    throw new InvalidDataException(
                        $"{SqliteSourceOpenPolicy.ExportCapExceededCode}: column '{name}' of query '{query.Name}' on '{sourcePath}' holds {bytes} bytes, larger than the export cap of {cellCapBytes} bytes.");
                }
            }

            return reader.GetValue(ordinal);
        }

        /// <summary>
        /// Runs one step with the deadline armed for the time this statement has left, so only time spent
        /// inside SQLite counts against the timeout, not time the caller spends between rows.
        /// </summary>
        private T Step<T>(HarnessSqliteQuery query, ref long remaining, Func<T> step)
        {
            long started = timestamp();
            Volatile.Write(ref deadline, started + remaining);
            try
            {
                return step();
            }
            catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xff) == SqliteInterrupt)
            {
                throw new IOException(
                    $"{SqliteSourceOpenPolicy.SourceUnreadableCode}: query '{query.Name}' on '{sourcePath}' exceeded the command timeout of {commandTimeoutSeconds} s.",
                    ex);
            }
            catch (SqliteException ex)
            {
                throw new IOException(
                    $"{SqliteSourceOpenPolicy.SourceUnreadableCode}: query '{query.Name}' on '{sourcePath}' failed with SQLite error {ex.SqliteErrorCode}: {ex.Message}",
                    ex);
            }
            finally
            {
                Volatile.Write(ref deadline, long.MaxValue);
                remaining -= timestamp() - started;
            }
        }
    }

    /// <summary>
    /// Rewrites each bare <c>?</c> marker to <c>?1</c>, <c>?2</c> in order, because <c>Microsoft.Data.Sqlite</c>
    /// binds by name only. Markers inside string literals, quoted identifiers and comments are left alone; a
    /// numbered or named marker, or a count that differs from the parameters, is refused.
    /// </summary>
    internal static string NumberPositionalParameters(string sql, int parameterCount, string queryName)
    {
        StringBuilder result = new(sql.Length + 8);
        int markers = 0;
        int i = 0;
        while (i < sql.Length)
        {
            char current = sql[i];
            int end = current switch
            {
                '\'' or '"' or '`' => ClosingQuote(sql, i, current),
                '[' => IndexAfter(sql, i + 1, "]"),
                '-' when i + 1 < sql.Length && sql[i + 1] == '-' => IndexAfter(sql, i + 2, "\n"),
                '/' when i + 1 < sql.Length && sql[i + 1] == '*' => IndexAfter(sql, i + 2, "*/"),
                _ => -1,
            };
            if (end >= 0)
            {
                result.Append(sql, i, end - i);
                i = end;
                continue;
            }

            if (current is '?')
            {
                if (i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1]))
                {
                    throw new ArgumentException($"Query '{queryName}' uses a numbered '?N' marker; use bare '?' markers.", nameof(sql));
                }

                markers++;
                result.Append('?').Append(markers.ToString(CultureInfo.InvariantCulture));
            }
            else if (current is ':' or '@' or '$' && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                throw new ArgumentException($"Query '{queryName}' uses a named '{current}' marker; use bare '?' markers.", nameof(sql));
            }
            else
            {
                result.Append(current);
            }

            i++;
        }

        if (markers != parameterCount)
        {
            throw new ArgumentException(
                $"Query '{queryName}' has {markers} '?' markers but {parameterCount} parameters.", nameof(parameterCount));
        }

        return result.ToString();
    }

    /// <summary>Index after the quote that closes the one at <paramref name="start"/>; a doubled quote is an escape.</summary>
    private static int ClosingQuote(string sql, int start, char quote)
    {
        int i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    private static int IndexAfter(string sql, int from, string terminator)
    {
        int found = sql.IndexOf(terminator, from, StringComparison.Ordinal);
        return found < 0 ? sql.Length : found + terminator.Length;
    }
}
