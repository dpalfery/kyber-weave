using KyberWeave.Core.Docs.Analysis.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The analysis cache's operational failures: every cause the catch accepts maps to its own remedy, and a
/// failure that is not operational (a lock, a corrupt file) is left to its own handler.
/// </summary>
public sealed class SqliteAnalysisPersistenceFailureTests
{
    [Theory]
    [InlineData(13, "database or disk is full", "DiskFull", "Free disk space")]
    [InlineData(1, "database or disk is full", "DiskFull", "Free disk space")]
    [InlineData(10, "disk I/O error", "DiskIo", "I/O error")]
    [InlineData(9, "interrupted", "Interrupted", "run the command again")]
    [InlineData(3, "access permission denied", "Access", "readable and writable")]
    [InlineData(8, "attempt to write a readonly database", "Access", "readable and writable")]
    [InlineData(14, "unable to open database file", "Access", "readable and writable")]
    public void EachOperationalCauseReportsItsOwnRemedy(int code, string message, string cause, string remedy)
    {
        SqliteException failure = new(message, code);

        Assert.Equal(Enum.Parse<OperationalCause>(cause), SqliteAnalysisPersistence.OperationalCauseOf(failure));
        Assert.Contains(remedy, SqliteAnalysisPersistence.OperationalRemedy(failure), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, "database is locked")]
    [InlineData(11, "database disk image is malformed")]
    public void ALockOrACorruptFileIsNotAnOperationalFailure(int code, string message) =>
        Assert.Null(SqliteAnalysisPersistence.OperationalCauseOf(new SqliteException(message, code)));
}
