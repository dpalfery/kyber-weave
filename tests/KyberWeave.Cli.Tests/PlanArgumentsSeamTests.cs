using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// How a plan argument finds its repository, resolves to a file and is announced, over the shared file-system, path,
/// environment and console fakes: nothing here touches the disk or the host's console.
/// </summary>
public sealed class PlanArgumentsSeamTests : IDisposable
{
    private const string WorkingDirectory = "/kw/work";

    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory);

    public PlanArgumentsSeamTests()
    {
        _paths = new FakePathResolver(_files);
        _console.Profile.Width = 400;
        _files.CreateDirectory(WorkingDirectory);
    }

    public void Dispose() => _console.Dispose();

    [Fact]
    public void Find_StopsAtTheNearestDirectoryHoldingAGitDirectory()
    {
        _files.CreateDirectory("/kw/work/repo/.git");
        _files.CreateDirectory("/kw/work/repo/docs/plans");

        Assert.Equal("/kw/work/repo", Roots().Find("/kw/work/repo/docs/plans"));
    }

    [Fact]
    public void Find_AGitFileInAWorktreeCountsAsTheRoot()
    {
        _files.CreateDirectory("/kw/work/tree/docs");
        _files.WriteAllText("/kw/work/tree/.git", "gitdir: /elsewhere");

        Assert.Equal("/kw/work/tree", Roots().Find("/kw/work/tree/docs"));
    }

    [Fact]
    public void Find_OutsideAnyRepository_ReturnsTheResolvedStart()
    {
        _files.CreateDirectory("/kw/work/loose");

        Assert.Equal("/kw/work/loose", Roots().Find("loose"));
    }

    [Fact]
    public void RepositoryRootFor_AnArgumentInsideARepository_NamesThatRepository()
    {
        _files.CreateDirectory("/kw/work/repo/.git");
        _files.CreateDirectory("/kw/work/repo/docs/plans");

        Assert.Equal("/kw/work/repo", Plans().RepositoryRootFor("repo/docs/plans/p.md"));
    }

    [Fact]
    public void RepositoryRootFor_AnArgumentInNoRepositoryUnderTheWorkingDirectory_NamesTheWorkingDirectory()
    {
        Assert.Equal(WorkingDirectory, Plans().RepositoryRootFor("docs/p.md"));
    }

    [Fact]
    public void RepositoryRootFor_AWorkingDirectoryOverride_IsUsedInPlaceOfTheEnvironmentsOne()
    {
        _files.CreateDirectory("/kw/other/.git");
        _files.CreateDirectory("/kw/other/docs");

        Assert.Equal("/kw/other", Plans().RepositoryRootFor("docs/p.md", "/kw/other"));
    }

    [Fact]
    public void Announce_ForAHumanFormat_WritesThePlanLineAndRecordsTheMetric()
    {
        DiagnosticReport report = new();

        Plans().Announce(report, new AnalysisSettings { Format = "lines" }, "docs/plans/p.md");

        Assert.Equal("Plan: docs/plans/p.md", _console.Output.Trim());
        Assert.Equal("docs/plans/p.md", report.Metrics["plan"]);
    }

    [Fact]
    public void Announce_ForAStructuredFormat_RecordsTheMetricAndWritesNothing()
    {
        DiagnosticReport report = new();

        Plans().Announce(report, new AnalysisSettings { Format = "json" }, "docs/plans/p.md");

        Assert.Empty(_console.Output);
        Assert.Equal("docs/plans/p.md", report.Metrics["plan"]);
    }

    private ArbiterRepoRoot Roots() => new(_files, _paths, _environment);

    private PlanArguments Plans() => new(Roots(), _files, _paths, _environment, _console);
}
