using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Processes;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The CodeGraph reader over an in-memory checkout and a scripted <c>sqlite3</c>; the real executable is proven by the integration tests.</summary>
public sealed class CodeGraphResolverAdapterSeamTests
{
    private const char Sep = '\u001f';

    private readonly InMemoryFileSystem _files = new();

    private string WorkingDirectory => Path.Combine(_files.Root, "work");

    private string Root => Path.Combine(WorkingDirectory, "repo");

    private string Database => Path.Combine(Root, ".codegraph", "codegraph.db");

    [Fact]
    public void AnIndexThatIsThereIsReadOnceAndAnswersSymbolRouteAndPathQueries()
    {
        Index();
        ScriptedProcessExecutor sqlite = new(_ => Output(
            Line("node", "n1", "class", "Widget", "Shop.Widget", "src/Widget.cs", "csharp", "3", "9"),
            Line("node", "n2", "route", "GET /api/widgets", "", "src/Api.cs", "csharp", "5", "5"),
            Line("edge", "n2", "n1", "calls", "", "", "", "", "")));

        CodeGraphResolverAdapter graph = Open(sqlite, Database);

        Assert.True(graph.IsAvailable);
        Assert.Equal("Widget", Assert.Single(graph.ResolveSymbol("Shop.Widget")).Name);
        Assert.Equal("n2", Assert.Single(graph.ResolveRoute("GET /api/widgets")).Id);
        Assert.True(graph.HasFilesUnder("src"));
        Assert.False(graph.HasFilesUnder("docs"));
        Assert.Single(graph.GetEdges(["n1", "n2"], maxDegree: 5));
        ProcessStartInfo call = Assert.Single(sqlite.CallsTo("sqlite3"));
        Assert.Contains(Database, call.ArgumentList);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(sqlite.Timeouts));
    }

    [Fact]
    public void AMissingIndexIsUnavailableAndSqliteIsNeverStarted()
    {
        ScriptedProcessExecutor sqlite = new(_ => Output());

        CodeGraphResolverAdapter graph = Open(sqlite, Database);

        Assert.False(graph.IsAvailable);
        Assert.Contains(Database, graph.UnavailableReason, StringComparison.Ordinal);
        Assert.Empty(sqlite.Calls);
        Assert.Empty(graph.ResolveSymbol("Widget"));
    }

    [Fact]
    public void ARelativeIndexPathIsResolvedAgainstTheWorkingDirectory()
    {
        Index();
        ScriptedProcessExecutor sqlite = new(_ => Output());

        CodeGraphResolverAdapter graph = Open(sqlite, ".codegraph/codegraph.db", currentDirectory: Root);

        Assert.Equal(Database, graph.DatabasePath);
        Assert.True(graph.IsAvailable);
    }

    [Fact]
    public void ASqliteThatFailsMakesTheIndexUnavailableAndSaysWhy()
    {
        Index();
        ScriptedProcessExecutor sqlite = new(_ => new ProcessResult(1, "", "no such table: nodes"));

        CodeGraphResolverAdapter graph = Open(sqlite, Database);

        Assert.False(graph.IsAvailable);
        Assert.Contains("exit 1", graph.UnavailableReason, StringComparison.Ordinal);
        Assert.Contains("no such table: nodes", graph.UnavailableReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(TimeoutException))]
    public void ASqliteThatCannotRunOrHangsMakesTheIndexUnavailable(Type failure)
    {
        Index();
        ScriptedProcessExecutor sqlite = new(_ => throw (Exception)Activator.CreateInstance(failure)!);

        CodeGraphResolverAdapter graph = Open(sqlite, Database);

        Assert.False(graph.IsAvailable);
        Assert.NotNull(graph.UnavailableReason);
    }

    [Fact]
    public void TheRecordedCommitAndPerFileStateComeFromASeparateRead()
    {
        Index();
        ScriptedProcessExecutor sqlite = new(call => call.ArgumentList[^1].Contains("project_metadata", StringComparison.Ordinal)
            ? Output(Line("file", "src/A.cs", "1700", "4"), Line("meta", "indexed_at_commit", "abc123", ""))
            : Output());
        CodeGraphResolverAdapter graph = Open(sqlite, Database);

        (string? commit, IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files) = graph.ReadStalenessFacts();

        Assert.Equal("abc123", commit);
        Assert.Equal((1700L, 4), files["src/A.cs"]);
        Assert.Equal(2, sqlite.CallsTo("sqlite3").Count());
    }

    private void Index()
    {
        _files.CreateDirectory(Path.Combine(Root, ".codegraph"));
        _files.WriteAllText(Database, "index");
    }

    private CodeGraphResolverAdapter Open(ScriptedProcessExecutor sqlite, string path, string? currentDirectory = null)
    {
        FakePathResolver paths = new(_files);
        return new CodeGraphResolverAdapter(
            path,
            _files,
            paths,
            new FakeProcessEnvironment(currentDirectory: currentDirectory ?? WorkingDirectory),
            sqlite,
            new CodeGraphIndexStaleness(_files, new ScriptedProcessRunner()));
    }

    private static ProcessResult Output(params string[] lines) => new(0, string.Join('\n', lines), string.Empty);

    private static string Line(params string[] fields) => string.Join(Sep, fields);
}
