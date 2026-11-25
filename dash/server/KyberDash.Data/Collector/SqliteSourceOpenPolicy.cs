namespace KyberDash.Data.Collector.Sources;

/// <summary>Why the plain read-only open of a harness database failed.</summary>
public enum SqliteOpenFailure
{
    /// <summary>The plain open succeeded.</summary>
    None,

    /// <summary>The directory is read-only, so SQLite cannot create its sidecar files.</summary>
    ReadOnlyDirectory,

    /// <summary>The open failed because a <c>-wal</c> or <c>-shm</c> sidecar is missing or unusable.</summary>
    MissingSidecar,

    /// <summary>The database is busy or locked.</summary>
    Busy,

    /// <summary>The database file does not exist.</summary>
    Missing,

    /// <summary>The source is not readable by this user.</summary>
    PermissionDenied,
}

/// <summary>What the adapter does after a failed open.</summary>
public enum SqliteOpenActionKind
{
    /// <summary>Use the plain read-only connection.</summary>
    Open,

    /// <summary>Reopen as <c>file:&lt;path&gt;?immutable=1</c>.</summary>
    Immutable,

    /// <summary>Copy the database and its WAL into the cache and open the copy read-only.</summary>
    CacheCopy,

    /// <summary>Do not retry; record <see cref="SqliteOpenAction.ProblemCode"/>.</summary>
    Refuse,
}

/// <summary>The decided open action; <see cref="ProblemCode"/> is set only for <see cref="SqliteOpenActionKind.Refuse"/>.</summary>
/// <param name="Kind">The action to take.</param>
/// <param name="ProblemCode">The recorded problem code when the source is refused; otherwise <see langword="null"/>.</param>
public readonly record struct SqliteOpenAction(SqliteOpenActionKind Kind, string? ProblemCode = null)
{
    /// <summary>Use the plain read-only connection.</summary>
    public static SqliteOpenAction Open { get; } = new(SqliteOpenActionKind.Open);

    /// <summary>Reopen the database immutable.</summary>
    public static SqliteOpenAction Immutable { get; } = new(SqliteOpenActionKind.Immutable);

    /// <summary>Copy the database and WAL to the cache.</summary>
    public static SqliteOpenAction CacheCopy { get; } = new(SqliteOpenActionKind.CacheCopy);

    /// <summary>Refuse the source with <paramref name="code"/>.</summary>
    public static SqliteOpenAction Refuse(string code) => new(SqliteOpenActionKind.Refuse, code);
}

/// <summary>Problem codes and the pure open-and-fallback decision for harness SQLite sources.</summary>
/// <remarks>
/// The decision is a pure function so it is unit tested; the adapter only executes it. It mirrors the
/// former TypeScript <c>openDatabase</c>/<c>openReadonlyCache</c>: an immutable open is safe only when the
/// WAL holds nothing, because immutable ignores the WAL and would hide committed rows. A missing file or a
/// permission denial is never retried, since a copy would fail the same way.
/// </remarks>
public static class SqliteSourceOpenPolicy
{
    /// <summary>The source is missing or cannot be read.</summary>
    public const string SourceUnreadableCode = "SQLITE_SOURCE_UNREADABLE";

    /// <summary>A table or column the reader needs is absent.</summary>
    public const string SchemaMissingCode = "SQLITE_SOURCE_SCHEMA_MISSING";

    /// <summary>An export read more than the export cap.</summary>
    public const string ExportCapExceededCode = "SQLITE_EXPORT_CAP_EXCEEDED";

    /// <summary>The database plus WAL is larger than the copy cap.</summary>
    public const string CopyCapExceededCode = "SQLITE_COPY_CAP_EXCEEDED";

    /// <summary>Decides what to do after the plain open reported <paramref name="failure"/>.</summary>
    /// <param name="failure">How the plain open failed.</param>
    /// <param name="walBytes">Size of the <c>-wal</c> file; zero when absent or empty.</param>
    /// <param name="databaseBytes">Size of the database file.</param>
    /// <param name="copyCapBytes">Most database-plus-WAL bytes that may be copied.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="failure"/> is not a defined value, or a size or the cap is negative.
    /// </exception>
    public static SqliteOpenAction Decide(SqliteOpenFailure failure, long walBytes, long databaseBytes, long copyCapBytes)
    {
        // A negative size is never a real file length; reading it as an empty WAL would open immutable and hide rows.
        ArgumentOutOfRangeException.ThrowIfNegative(walBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(databaseBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(copyCapBytes);
        switch (failure)
        {
            case SqliteOpenFailure.None:
                return SqliteOpenAction.Open;
            case SqliteOpenFailure.ReadOnlyDirectory:
            case SqliteOpenFailure.MissingSidecar:
            case SqliteOpenFailure.Busy:
                if (walBytes <= 0)
                {
                    return SqliteOpenAction.Immutable;
                }

                // Compared by subtraction so two large sizes cannot overflow their sum past the cap.
                return databaseBytes <= copyCapBytes && walBytes <= copyCapBytes - databaseBytes
                    ? SqliteOpenAction.CacheCopy
                    : SqliteOpenAction.Refuse(CopyCapExceededCode);
            case SqliteOpenFailure.Missing:
            case SqliteOpenFailure.PermissionDenied:
                return SqliteOpenAction.Refuse(SourceUnreadableCode);
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown SQLite open failure.");
        }
    }
}
