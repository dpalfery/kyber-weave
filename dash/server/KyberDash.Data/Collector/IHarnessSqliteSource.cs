namespace KyberDash.Data.Collector.Sources;

/// <summary>
/// Read-only seam over a harness-owned SQLite database. A reader depends on this interface so unit tests
/// supply canned rows and open no file; only the Microsoft.Data.Sqlite adapter touches the disk.
/// </summary>
public interface IHarnessSqliteSource
{
    /// <summary>
    /// Opens the database at <paramref name="databasePath"/> read-only, applying the fallback in
    /// <see cref="SqliteSourceOpenPolicy"/>.
    /// </summary>
    /// <exception cref="IOException">The source cannot be read; the message carries the problem code.</exception>
    IHarnessSqliteDatabase Open(string databasePath);
}

/// <summary>An open read-only harness database. Disposing it releases the connection and any cached copy handle.</summary>
public interface IHarnessSqliteDatabase : IDisposable
{
    /// <summary>Runs <paramref name="query"/> and yields its rows one at a time, keyed by column name.</summary>
    IEnumerable<IReadOnlyDictionary<string, object?>> Query(HarnessSqliteQuery query);

    /// <summary>Returns the column names of <paramref name="table"/>, or an empty set when the table is absent.</summary>
    IReadOnlySet<string> GetColumns(string table);
}

/// <summary>A named, parameterised read query. <paramref name="Name"/> identifies it to fakes and logs.</summary>
/// <param name="Name">Stable query name; the in-memory fake keys canned rows by it.</param>
/// <param name="Sql">The SELECT text, with positional <c>?</c> parameters.</param>
/// <param name="Parameters">Values bound positionally; never concatenated into <paramref name="Sql"/>.</param>
public record HarnessSqliteQuery(string Name, string Sql, IReadOnlyList<object?> Parameters);

/// <summary>Size and time limits for reading a harness database.</summary>
/// <param name="ExportCapBytes">
/// Most bytes (64 MiB) one export may read, and one companion file may hold, before a recorded problem.
/// </param>
/// <param name="CopyCapBytes">Most database-plus-WAL bytes the cache-copy fallback may copy (4 GiB).</param>
/// <param name="CommandTimeoutSeconds">Per-command timeout.</param>
/// <param name="BusyTimeoutMilliseconds">SQLite <c>busy_timeout</c>.</param>
public record HarnessSqliteLimits(
    long ExportCapBytes = 67_108_864,
    long CopyCapBytes = 4_294_967_296,
    int CommandTimeoutSeconds = 30,
    int BusyTimeoutMilliseconds = 1000);
