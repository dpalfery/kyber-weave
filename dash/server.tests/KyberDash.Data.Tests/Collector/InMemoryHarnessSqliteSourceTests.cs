using KyberDash.Data.Collector.Sources;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>Pins the in-memory fake every non-adapter SQLite reader test builds on.</summary>
public class InMemoryHarnessSqliteSourceTests
{
    private const string DbPath = "/fake/state.db";

    [Fact]
    public void Query_ReturnsCannedRowsForTheQueryNameInInsertionOrder()
    {
        InMemoryHarnessSqliteSource source = new();
        source.AddDatabase(DbPath)
            .AddRow("sessions", new Dictionary<string, object?> { ["id"] = "a" })
            .AddRow("sessions", new Dictionary<string, object?> { ["id"] = "b" })
            .AddRow("messages", new Dictionary<string, object?> { ["id"] = "m" });

        using IHarnessSqliteDatabase database = source.Open(DbPath);
        List<IReadOnlyDictionary<string, object?>> rows = [.. database.Query(new HarnessSqliteQuery("sessions", "select 1", []))];

        Assert.Equal(["a", "b"], rows.Select(row => (string?)row["id"]));
    }

    [Fact]
    public void Query_UnknownName_YieldsNoRows()
    {
        InMemoryHarnessSqliteSource source = new();
        source.AddDatabase(DbPath);

        using IHarnessSqliteDatabase database = source.Open(DbPath);

        Assert.Empty(database.Query(new HarnessSqliteQuery("absent", "select 1", [])));
    }

    [Fact]
    public void Query_RecordsParametersForAssertions()
    {
        InMemoryHarnessSqliteSource source = new();
        source.AddDatabase(DbPath);

        using (IHarnessSqliteDatabase database = source.Open(DbPath))
        {
            _ = database.Query(new HarnessSqliteQuery("sessions", "select ?", ["x", 7L])).ToList();
        }

        HarnessSqliteQuery recorded = Assert.Single(source.ExecutedQueries(DbPath));
        Assert.Equal("sessions", recorded.Name);
        Assert.Equal(new object?[] { "x", 7L }, recorded.Parameters);
    }

    [Fact]
    public void GetColumns_ReturnsConfiguredColumnsAndEmptyForUnknownTable()
    {
        InMemoryHarnessSqliteSource source = new();
        source.AddDatabase(DbPath).AddTable("sessions", "id", "title");

        using IHarnessSqliteDatabase database = source.Open(DbPath);

        Assert.Equal(new HashSet<string> { "id", "title" }, database.GetColumns("sessions"));
        Assert.Empty(database.GetColumns("nope"));
    }

    [Fact]
    public void Open_PathConfiguredToFail_ThrowsIoExceptionNamingTheUnreadableCode()
    {
        InMemoryHarnessSqliteSource source = new();
        source.AddDatabase(DbPath);
        source.FailOpen(DbPath);

        IOException error = Assert.Throws<IOException>(() => source.Open(DbPath));

        Assert.Contains(SqliteSourceOpenPolicy.SourceUnreadableCode, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_PathNeverConfigured_ThrowsIoException()
    {
        InMemoryHarnessSqliteSource source = new();

        Assert.Throws<IOException>(() => source.Open("/fake/other.db"));
    }
}
