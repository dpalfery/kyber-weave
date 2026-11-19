using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using KyberWeave.Data.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.RunStart;

/// <summary><c>run start</c> records the run's autonomy; a record written before that reads as unknown.</summary>
public sealed class RunRecordAutonomyTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly FileRunStateStore _state;
    private readonly string _home;

    public RunRecordAutonomyTests()
    {
        _state = new FileRunStateStore(
            _files,
            new FakeCheckedOutBranchReader(),
            new FakePathResolver(_files),
            new FakeProcessEnvironment());
        _home = Path.Combine(_files.Root, "home");
    }

    private RunRecord Written(string? autonomy)
    {
        _state.Write(
            _home,
            "claude/feat/x",
            "origin/main",
            new RunStartPlace("/kw/repo/.git", "/kw/repo", DateTimeOffset.UnixEpoch, autonomy));
        return Assert.Single(_state.ReadRecords(_home)!);
    }

    [Theory]
    [InlineData("autonomous")]
    [InlineData("supervised")]
    [InlineData(null)]
    public void TheRecordedAutonomyReadsBack(string? autonomy) =>
        Assert.Equal(autonomy, Written(autonomy).Autonomy);

    [Fact]
    public void ARecordWrittenBeforeTheFieldExistedReadsAsUnknown()
    {
        string path = RunBaseStateFormat.StatePath(_home, "claude/feat/x");
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(
            path,
            """{"BaseRef":"origin/main","Repository":"/kw/repo/.git","Branch":"claude/feat/x","Worktree":"/kw/repo","StartedAt":"2026-11-18T12:00:00+00:00"}""");

        RunRecord record = Assert.Single(_state.ReadRecords(_home)!);

        Assert.Null(record.Autonomy);
    }
}
