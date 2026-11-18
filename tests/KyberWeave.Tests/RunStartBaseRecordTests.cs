using KyberWeave.Application.RunStart;
using KyberWeave.Cli.Commands.Run;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.RunStart;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The base record <c>run start</c> leaves for later hooks: best effort, never a reason a
/// ready run fails.
/// </summary>
public sealed class RunStartBaseRecordTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IRunStateStore _state = RunStartTestServices.RunStateStore();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>A recorded base reads back for the hook that follows.</summary>
    [Fact]
    public void ARecordedBaseReadsBack()
    {
        Assert.True(RunStartService.RecordBase(_state, _temp.Path, "claude/fix/tighten-the-gate", "origin/main"));
        Assert.True(
            _state.TryReadBaseRef(_temp.Path, "claude/fix/tighten-the-gate", out string? baseRef));
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
