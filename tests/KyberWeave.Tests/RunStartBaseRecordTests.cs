using KyberWeave.Cli.Commands.Run;
using KyberWeave.Core.RunStart;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The base record <c>run start</c> leaves for later hooks: best effort, never a reason a
/// ready run fails.
/// </summary>
public sealed class RunStartBaseRecordTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>A recorded base reads back for the hook that follows.</summary>
    [Fact]
    public void ARecordedBaseReadsBack()
    {
        Assert.True(RunStartCommand.RecordBase(_temp.Path, "claude/fix/tighten-the-gate", "origin/main"));
        Assert.True(
            RunBaseState.TryReadBaseRef(_temp.Path, "claude/fix/tighten-the-gate", out string? baseRef));
        Assert.Equal("origin/main", baseRef);
    }

    /// <summary>
    /// No home to record under skips the record instead of failing the run: the hook falls
    /// back to its default base.
    /// </summary>
    [Fact]
    public void ABlankHomeSkipsTheRecord()
    {
        Assert.False(RunStartCommand.RecordBase("  ", "claude/fix/tighten-the-gate", "origin/main"));
    }
}
