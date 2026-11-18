using KyberWeave.Application.RunStart;
using KyberWeave.Cli.Commands.Run;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using KyberWeave.Data.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The base record <c>run start</c> leaves for later hooks: best effort, never a reason a
/// ready run fails.
/// </summary>
public sealed class RunStartBaseRecordTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly IRunStateStore _state;
    private readonly string _home;

    public RunStartBaseRecordTests()
    {
        _state = new FileRunStateStore(
            _files,
            new FakeCheckedOutBranchReader(),
            new FakePathResolver(_files),
            new FakeProcessEnvironment());
        _home = Path.Combine(_files.RootPath, "home");
    }

    /// <summary>A recorded base reads back for the hook that follows.</summary>
    [Fact]
    public void ARecordedBaseReadsBack()
    {
        Assert.True(RunStartService.RecordBase(_state, _home, "claude/fix/tighten-the-gate", "origin/main"));
        Assert.True(
            _state.TryReadBaseRef(_home, "claude/fix/tighten-the-gate", out string? baseRef));
        Assert.Equal("origin/main", baseRef);
    }

    /// <summary>
    /// No home to record under skips the record instead of failing the run: the hook falls
    /// back to its default base.
    /// </summary>
    [Fact]
    public void ABlankHomeSkipsTheRecord()
    {
        Assert.False(RunStartService.RecordBase(_state, "  ", "claude/fix/tighten-the-gate", "origin/main"));
    }
}
