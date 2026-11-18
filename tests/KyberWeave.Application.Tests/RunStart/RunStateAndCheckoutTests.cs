using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using KyberWeave.Data.RunStart;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.RunStart;

/// <summary>The run records and the canonical-checkout guard over an in-memory file system and a scripted branch reader.</summary>
public sealed class RunStateAndCheckoutTests
{
    private const string Home = "/home/dev";
    private const string Canonical = "/work/repo";
    private const string Linked = "/work/repo-run";

    private readonly InMemoryFileSystem _files = new();
    private readonly Dictionary<string, string?> _branches = [];

    private FileRunStateStore Store() => new(_files, new ScriptedBranchReader(_branches));

    private RunCheckoutGuard Guard() => new(_files, Store());

    private void Record(string worktree, string branch, string repository) =>
        Store().Write(Home, branch, "origin/main", new RunStartPlace(repository + "/.git", worktree, DateTimeOffset.UnixEpoch));

    [Fact]
    public void AWrittenBaseRefReadsBackAndAnUnrecordedBranchReadsAbsent()
    {
        FileRunStateStore store = Store();
        store.Write(Home, "claude/fix/a", "origin/main");

        Assert.True(store.TryReadBaseRef(Home, "claude/fix/a", out string? baseRef));
        Assert.Equal("origin/main", baseRef);
        Assert.False(store.TryReadBaseRef(Home, "claude/fix/never", out _));
    }

    [Fact]
    public void ARunIsLiveOnlyWhileItsWorktreeStillHasTheBranchCheckedOut()
    {
        _files.CreateDirectory(Linked);
        Record(Linked, "claude/fix/a", Canonical);
        _branches[Linked] = "claude/fix/a";

        Assert.True(Store().HasLiveRun(Home, Canonical + "/.git"));

        _branches[Linked] = "other";
        Assert.False(Store().HasLiveRun(Home, Canonical + "/.git"));
    }

    [Fact]
    public void AWriteInsideTheCanonicalCheckoutIsRefusedWhileALiveRunWorksInItsOwnWorktree()
    {
        _files.CreateDirectory(Canonical + "/.git");
        _files.CreateDirectory(Linked);
        _files.WriteAllText(Linked + "/.git", $"gitdir: {Canonical}/.git/worktrees/run");
        _files.CreateDirectory(Canonical + "/.git/worktrees/run");
        _files.WriteAllText(Canonical + "/.git/worktrees/run/HEAD", "ref: refs/heads/claude/fix/a\n");
        Record(Linked, "claude/fix/a", Canonical);

        string? refusal = Guard().StrayWriteRefusal(Canonical + "/src/File.cs", Canonical, Home);

        Assert.NotNull(refusal);
        Assert.Contains(Linked + "/src/File.cs", refusal, StringComparison.Ordinal);
        Assert.Null(Guard().StrayWriteRefusal(Linked + "/src/File.cs", Linked, Home));
        Assert.Null(Guard().StrayWriteRefusal(Canonical + "/.git/config", Canonical, Home));
    }

    [Fact]
    public void ACommonGitDirectoryNamesItsParentAsTheCanonicalCheckout()
    {
        Assert.Equal(Canonical, Guard().CanonicalCheckoutOf(Canonical + "/.git"));
        Assert.Null(Guard().CanonicalCheckoutOf(Canonical + "/elsewhere"));
    }

    private sealed class ScriptedBranchReader(Dictionary<string, string?> branches) : ICheckedOutBranchReader
    {
        public string? Read(string worktree) => branches.GetValueOrDefault(worktree);
    }
}
