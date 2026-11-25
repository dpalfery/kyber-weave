using KyberDash.Data.Collector.Sources;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// In-memory <see cref="IHarnessSqliteSource"/>: databases keyed by path, each with table columns and
/// canned rows per query name (returned in insertion order). Opens no file.
/// </summary>
public sealed class InMemoryHarnessSqliteSource : IHarnessSqliteSource
{
    private readonly Dictionary<string, FakeDatabase> databases = new(StringComparer.Ordinal);
    private readonly HashSet<string> failingPaths = new(StringComparer.Ordinal);

    /// <summary>Registers (or returns) the database at <paramref name="path"/>.</summary>
    public FakeDatabase AddDatabase(string path)
    {
        if (!databases.TryGetValue(path, out FakeDatabase? database))
        {
            database = new FakeDatabase();
            databases[path] = database;
        }

        return database;
    }

    /// <summary>Makes <see cref="Open"/> throw for <paramref name="path"/>, as an unreadable source does.</summary>
    public void FailOpen(string path) => failingPaths.Add(path);

    /// <summary>Queries run against the database at <paramref name="path"/>, in call order.</summary>
    public IReadOnlyList<HarnessSqliteQuery> ExecutedQueries(string path) =>
        databases.TryGetValue(path, out FakeDatabase? database) ? database.Executed : [];

    /// <inheritdoc />
    public IHarnessSqliteDatabase Open(string databasePath)
    {
        if (failingPaths.Contains(databasePath) || !databases.TryGetValue(databasePath, out FakeDatabase? database))
        {
            throw new IOException($"{SqliteSourceOpenPolicy.SourceUnreadableCode}: cannot open '{databasePath}'.");
        }

        return new OpenHandle(database);
    }

    private sealed class OpenHandle(FakeDatabase database) : IHarnessSqliteDatabase
    {
        public IEnumerable<IReadOnlyDictionary<string, object?>> Query(HarnessSqliteQuery query) => database.Run(query);

        public IReadOnlySet<string> GetColumns(string table) => database.Columns(table);

        public void Dispose()
        {
        }
    }
}

/// <summary>One fake database's tables and canned rows.</summary>
public sealed class FakeDatabase
{
    private readonly Dictionary<string, HashSet<string>> tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IReadOnlyDictionary<string, object?>>> rows = new(StringComparer.Ordinal);
    private readonly List<HarnessSqliteQuery> executed = [];

    internal IReadOnlyList<HarnessSqliteQuery> Executed => executed;

    /// <summary>Declares <paramref name="table"/> with <paramref name="columns"/>.</summary>
    public FakeDatabase AddTable(string table, params string[] columns)
    {
        tables[table] = new HashSet<string>(columns, StringComparer.Ordinal);
        return this;
    }

    /// <summary>Appends a canned row returned by queries named <paramref name="queryName"/>.</summary>
    public FakeDatabase AddRow(string queryName, IReadOnlyDictionary<string, object?> row)
    {
        if (!rows.TryGetValue(queryName, out List<IReadOnlyDictionary<string, object?>>? list))
        {
            list = [];
            rows[queryName] = list;
        }

        list.Add(row);
        return this;
    }

    internal IEnumerable<IReadOnlyDictionary<string, object?>> Run(HarnessSqliteQuery query)
    {
        executed.Add(query);
        return rows.TryGetValue(query.Name, out List<IReadOnlyDictionary<string, object?>>? list) ? list.ToArray() : [];
    }

    internal IReadOnlySet<string> Columns(string table) =>
        tables.TryGetValue(table, out HashSet<string>? columns) ? columns : new HashSet<string>();
}
