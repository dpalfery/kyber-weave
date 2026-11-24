using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// A <see cref="SqliteRelationalStore"/> subclass that records every script handed to the
/// <see cref="KyberDash.Core.Repositories.BaseSqliteRepository.ExecuteSqliteCore"/> seam, so a
/// test can assert which reads a code path issued and how each one is scoped — the keyed-read
/// and one-statement assertions behind the sessions, findings, and sectioned pages.
/// </summary>
public class SeamSqliteRelationalStore : SqliteRelationalStore
{
    private readonly List<string> executedSql = [];

    /// <summary>Creates a recording store over a new database file.</summary>
    /// <param name="databasePath">Database file path.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding execution.</param>
    public SeamSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
        : base(databasePath, circuitBreaker)
    {
    }

    /// <summary>Every script executed since <see cref="ClearRecordedSql"/> was last called.</summary>
    public IReadOnlyList<string> ExecutedSql => executedSql;

    /// <summary>Clears the recorded scripts.</summary>
    public void ClearRecordedSql() => executedSql.Clear();

    /// <inheritdoc />
    protected override string ExecuteSqliteCore(string sql)
    {
        executedSql.Add(sql);
        return base.ExecuteSqliteCore(sql);
    }
}
