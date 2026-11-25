using KyberDash.Core.Repositories;
using Microsoft.Data.Sqlite;

namespace KyberDash.Data.Tests.Collector;

/// <summary>How a fixture database leaves its journal and sidecar files when it is closed.</summary>
public enum SqliteFixtureJournal
{
    /// <summary>Rollback-journal mode: the rows are in the database file and no sidecar is left beside it.</summary>
    Rollback,

    /// <summary>WAL mode with every row checkpointed into the database, a zero-byte <c>-wal</c> and no <c>-shm</c>.</summary>
    WalEmpty,

    /// <summary>WAL mode with the rows only in a non-empty <c>-wal</c> and no <c>-shm</c>, as a live harness leaves it.</summary>
    WalUncheckpointed,
}

/// <summary>One parameterised statement run against a fixture database.</summary>
/// <param name="Sql">The statement, with numbered <c>?1</c>, <c>?2</c> parameters.</param>
/// <param name="Parameters">Values bound to <c>?1</c>, <c>?2</c> in order.</param>
public sealed record SqliteFixtureStatement(string Sql, IReadOnlyList<object?> Parameters);

/// <summary>Creates real SQLite files for adapter tests, with the journal state each test needs.</summary>
/// <remarks>
/// The sidecars are set explicitly rather than left to SQLite, because the macOS system library keeps
/// <c>-wal</c> and <c>-shm</c> after the last close while Linux deletes them: a test that relied on
/// either would pass or fail by platform rather than by the adapter's behaviour.
/// </remarks>
public static class SqliteFixtureDatabase
{
    /// <summary>
    /// Creates (or replaces) <paramref name="databasePath"/>, runs <paramref name="ddl"/> and
    /// <paramref name="statements"/>, and leaves it in the <paramref name="journal"/> state.
    /// </summary>
    /// <returns><paramref name="databasePath"/>.</returns>
    public static string Create(
        string databasePath,
        string ddl,
        IReadOnlyList<SqliteFixtureStatement> statements,
        SqliteFixtureJournal journal = SqliteFixtureJournal.Rollback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(ddl);
        ArgumentNullException.ThrowIfNull(statements);
        SqliteProvider.EnsureInitialized();

        DeleteWithSidecars(databasePath);
        if (journal != SqliteFixtureJournal.WalUncheckpointed)
        {
            using (SqliteConnection connection = OpenWritable(databasePath))
            {
                if (journal == SqliteFixtureJournal.WalEmpty)
                {
                    Execute(connection, "PRAGMA journal_mode=WAL;", []);
                }

                Populate(connection, ddl, statements);
                if (journal == SqliteFixtureJournal.WalEmpty)
                {
                    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);", []);
                }
            }

            if (journal == SqliteFixtureJournal.WalEmpty)
            {
                File.WriteAllBytes(databasePath + "-wal", []);
                File.Delete(databasePath + "-shm");
            }

            return databasePath;
        }

        // The rows must stay in the WAL after the writer closes, but a closing writer checkpoints. So the
        // database and its WAL are copied out while the writer is still open, then the staging copy goes.
        string staging = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? Path.GetTempPath(),
            ".fixture-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string stagingDatabase = Path.Combine(staging, "staging.db");
            using (SqliteConnection connection = OpenWritable(stagingDatabase))
            {
                Execute(connection, "PRAGMA journal_mode=WAL;", []);
                Execute(connection, "PRAGMA wal_autocheckpoint=0;", []);
                Populate(connection, ddl, statements);
                File.Copy(stagingDatabase, databasePath);
                File.Copy(stagingDatabase + "-wal", databasePath + "-wal");
            }
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }

        return databasePath;
    }

    private static SqliteConnection OpenWritable(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Populate(SqliteConnection connection, string ddl, IReadOnlyList<SqliteFixtureStatement> statements)
    {
        Execute(connection, ddl, []);
        foreach (SqliteFixtureStatement statement in statements)
        {
            Execute(connection, statement.Sql, statement.Parameters);
        }
    }

    // reason: the SQL is the calling test's own literal text, never outside input; values are bound as parameters.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test fixture runs the test's own literal DDL; values are bound as parameters.")]
    private static void Execute(SqliteConnection connection, string sql, IReadOnlyList<object?> parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        for (int i = 0; i < parameters.Count; i++)
        {
            command.Parameters.AddWithValue("?" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), parameters[i] ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static void DeleteWithSidecars(string databasePath)
    {
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            File.Delete(databasePath + suffix);
        }
    }
}
