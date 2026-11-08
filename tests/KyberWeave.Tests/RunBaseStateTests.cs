using System.Text.Json;
using KyberWeave.Core.RunStart;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The run's recorded base: <c>run start</c> writes the base ref it branched from to a
/// branch-keyed file under the operator's home, so a later dispatch hook can measure the
/// review from the same base instead of guessing <c>origin/HEAD</c>.
/// </summary>
public sealed class RunBaseStateTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    /// A write followed by a read returns the base ref the run branched from.
    /// </summary>
    [Fact]
    public void AWrittenBaseRefReadsBack()
    {
        RunBaseState.Write(_temp.Path, "claude/fix/tighten-the-gate", "origin/main");

        Assert.True(
            RunBaseState.TryReadBaseRef(_temp.Path, "claude/fix/tighten-the-gate", out string? baseRef),
            "a recorded base ref must read back.");
        Assert.Equal("origin/main", baseRef);
    }

    /// <summary>
    /// No state file means no recorded base: the hook falls back to its default base rather
    /// than failing.
    /// </summary>
    [Fact]
    public void AMissingStateReadsAbsent()
    {
        Assert.False(
            RunBaseState.TryReadBaseRef(_temp.Path, "claude/fix/never-started", out string? baseRef),
            "a branch with no recorded run must read absent.");
        Assert.Null(baseRef);
    }

    /// <summary>
    /// A branch name with slashes stays a single file inside the runs directory: the key must
    /// never escape into subdirectories.
    /// </summary>
    [Fact]
    public void ASlashedBranchNameStaysASingleFile()
    {
        string path = RunBaseState.StatePath(_temp.Path, "claude/fix/tighten-the-gate");

        Assert.Equal(Path.Combine(_temp.Path, ".kyber-weave", "runs"), Path.GetDirectoryName(path));
        Assert.False(
            Path.GetFileName(path).Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"the state file name must hold no separator, not '{path}'.");
    }

    /// <summary>
    /// A later run overwrites the earlier record: a rebranched or rebased branch never reads
    /// a stale base.
    /// </summary>
    [Fact]
    public void ALaterWriteReplacesTheEarlierBaseRef()
    {
        RunBaseState.Write(_temp.Path, "claude/fix/tighten-the-gate", "origin/main");
        RunBaseState.Write(_temp.Path, "claude/fix/tighten-the-gate", "origin/develop");

        Assert.True(
            RunBaseState.TryReadBaseRef(_temp.Path, "claude/fix/tighten-the-gate", out string? baseRef),
            "a re-recorded base ref must read back.");
        Assert.Equal("origin/develop", baseRef);
    }

    /// <summary>
    /// A corrupt state file reads absent rather than crashing the hook that reads it.
    /// </summary>
    [Fact]
    public void ACorruptStateFileReadsAbsent()
    {
        RunBaseState.Write(_temp.Path, "claude/fix/tighten-the-gate", "origin/main");
        File.WriteAllText(RunBaseState.StatePath(_temp.Path, "claude/fix/tighten-the-gate"), "{nope");

        Assert.False(
            RunBaseState.TryReadBaseRef(_temp.Path, "claude/fix/tighten-the-gate", out _),
            "a corrupt state file must read absent, not throw.");
    }
}
