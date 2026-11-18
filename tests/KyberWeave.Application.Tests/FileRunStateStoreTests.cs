using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using KyberWeave.Data.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The run's recorded base: <c>run start</c> writes the base ref it branched from to a
/// branch-keyed file under the operator's home, so a later dispatch hook can measure the
/// review from the same base instead of guessing <c>origin/HEAD</c>.
/// </summary>
public sealed class FileRunStateStoreTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly IRunStateStore _state;
    private readonly string _home;

    public FileRunStateStoreTests()
    {
        _state = new FileRunStateStore(
            _files,
            new FakeCheckedOutBranchReader(),
            new FakePathResolver(_files),
            new FakeProcessEnvironment());
        _home = Path.Combine(_files.Root, "home");
    }

    /// <summary>
    /// A write followed by a read returns the base ref the run branched from.
    /// </summary>
    [Fact]
    public void AWrittenBaseRefReadsBack()
    {
        _state.Write(_home, "claude/fix/tighten-the-gate", "origin/main");

        Assert.True(
            _state.TryReadBaseRef(_home, "claude/fix/tighten-the-gate", out string? baseRef),
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
            _state.TryReadBaseRef(_home, "claude/fix/never-started", out string? baseRef),
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
        string path = RunBaseStateFormat.StatePath(_home, "claude/fix/tighten-the-gate");

        Assert.Equal(Path.Combine(_home, ".kyber-weave", "runs"), Path.GetDirectoryName(path));
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
        _state.Write(_home, "claude/fix/tighten-the-gate", "origin/main");
        _state.Write(_home, "claude/fix/tighten-the-gate", "origin/develop");

        Assert.True(
            _state.TryReadBaseRef(_home, "claude/fix/tighten-the-gate", out string? baseRef),
            "a re-recorded base ref must read back.");
        Assert.Equal("origin/develop", baseRef);
    }

    /// <summary>
    /// Branches whose sanitized names collide still keep separate records: the key carries
    /// a hash of the full name, so <c>a/b</c> never reads <c>a-b</c>'s base.
    /// </summary>
    [Fact]
    public void ACollidingBranchNameKeepsItsOwnRecord()
    {
        _state.Write(_home, "a/b", "origin/main");
        _state.Write(_home, "a-b", "origin/develop");

        Assert.True(
            _state.TryReadBaseRef(_home, "a/b", out string? first),
            "a slashed branch must keep its own record.");
        Assert.True(
            _state.TryReadBaseRef(_home, "a-b", out string? second),
            "a dashed branch must keep its own record.");
        Assert.Equal("origin/main", first);
        Assert.Equal("origin/develop", second);
    }

    /// <summary>
    /// A corrupt state file reads absent rather than crashing the hook that reads it.
    /// </summary>
    [Fact]
    public void ACorruptStateFileReadsAbsent()
    {
        _state.Write(_home, "claude/fix/tighten-the-gate", "origin/main");
        _files.WriteAllText(RunBaseStateFormat.StatePath(_home, "claude/fix/tighten-the-gate"), "{nope");

        Assert.False(
            _state.TryReadBaseRef(_home, "claude/fix/tighten-the-gate", out _),
            "a corrupt state file must read absent, not throw.");
    }
}
