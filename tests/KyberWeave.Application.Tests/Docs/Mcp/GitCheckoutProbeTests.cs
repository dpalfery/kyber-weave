using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Mcp;

/// <summary>
/// The checkout probe over the process and file-system seams: the git metadata lives in memory and git's answers
/// are scripted, so branch, repository, revision and fingerprint are proven without a repository or a git install.
/// </summary>
public sealed class GitCheckoutProbeTests
{
    private const string Root = "/work/repo";
    private static readonly string[] RevisionArgv = ["rev-parse", "--short", "HEAD"];
    private static readonly string[] StatusArgv = ["status", "--porcelain", "--untracked-files=no"];

    private readonly InMemoryFileSystem _files = new();
    private readonly ScriptedProcessRunner _git = new();

    private GitCheckoutProbe Probe() => new(_files, _git, new FakePathResolver(_files));

    private void Write(string path, string text)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, text);
    }

    [Fact]
    public void ANormalCheckoutReportsItsBranchAndItsOwnGitDirectoryAsTheRepository()
    {
        Write(Root + "/.git/HEAD", "ref: refs/heads/feature/x\n");

        GitCheckoutLocation location = Probe().ReadLocation(Root);

        Assert.Equal("feature/x", location.Branch);
        Assert.Equal(Root + "/.git", location.Repository);
    }

    [Fact]
    public void ALinkedWorktreeFollowsItsPointerAndNamesTheSharedGitDirectoryFromCommondir()
    {
        string metadata = "/work/main/.git/worktrees/wt";
        Write(Root + "/.git", "gitdir: " + metadata + "\n");
        Write(metadata + "/HEAD", "ref: refs/heads/topic\n");
        Write(metadata + "/commondir", "../..\n");

        GitCheckoutLocation location = Probe().ReadLocation(Root);

        Assert.Equal("topic", location.Branch);
        Assert.Equal(Path.GetFullPath("../..", metadata), location.Repository);
    }

    [Theory]
    [InlineData("ref: refs/remotes/origin/main", "remotes/origin/main")]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "detached")]
    [InlineData("", "unavailable")]
    public void HeadThatIsNotALocalBranchReportsTheRefDetachedOrUnavailable(string head, string expected)
    {
        Write(Root + "/.git/HEAD", head);

        Assert.Equal(expected, Probe().ReadLocation(Root).Branch);
    }

    [Fact]
    public void ADirectoryThatIsNotACheckoutIsUnavailableOnBothFieldsAndFingerprintsToZero()
    {
        _files.CreateDirectory(Root);

        GitCheckoutLocation location = Probe().ReadLocation(Root);

        Assert.Equal("unavailable", location.Branch);
        Assert.Equal("unavailable", location.Repository);
        Assert.Equal(0, Probe().Fingerprint(Root));
    }

    [Fact]
    public void ADotGitFileWithoutAGitdirPointerIsNotACheckout()
    {
        Write(Root + "/.git", "not a pointer");

        Assert.Equal("unavailable", Probe().ReadLocation(Root).Branch);
    }

    [Fact]
    public void ADotGitFileWithoutAGitdirPointerFingerprintsToZero()
    {
        Write(Root + "/.git", "not a pointer");

        Assert.Equal(0, Probe().Fingerprint(Root));
    }

    [Fact]
    public async Task TheRevisionIsGitsFirstLineAndACleanTreeIsNotDirty()
    {
        _ = _git
            .Script("git", new ProcessRunResult(0, "abc1234\n", false), RevisionArgv)
            .Script("git", new ProcessRunResult(0, "  \n", false), StatusArgv);

        GitCheckoutRevision revision = await Probe().ReadRevisionAsync(Root);

        Assert.Equal("abc1234", revision.Revision);
        Assert.Equal("no", revision.Dirty);
        Assert.All(_git.Calls, call =>
        {
            Assert.Equal(Root, call.WorkingDirectory);
            Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
        });
    }

    [Fact]
    public async Task AnyStatusOutputMeansDirty()
    {
        _ = _git
            .Script("git", new ProcessRunResult(0, "abc1234\n", false), RevisionArgv)
            .Script("git", new ProcessRunResult(0, " M file.cs\n", false), StatusArgv);

        Assert.Equal("yes", (await Probe().ReadRevisionAsync(Root)).Dirty);
    }

    [Fact]
    public async Task AFailedTimedOutOrMissingGitIsUnavailableAndUnknownNeverInvented()
    {
        _ = _git
            .Script("git", new ProcessRunResult(128, string.Empty, false), RevisionArgv)
            .ScriptTimeout("git", StatusArgv);

        GitCheckoutRevision failed = await Probe().ReadRevisionAsync(Root);

        Assert.Equal("unavailable", failed.Revision);
        Assert.Equal("unknown", failed.Dirty);

        ScriptedProcessRunner none = new ScriptedProcessRunner().WithMissingProgram("git");
        GitCheckoutRevision missing = await new GitCheckoutProbe(_files, none, new FakePathResolver(_files)).ReadRevisionAsync(Root);

        Assert.Equal("unavailable", missing.Revision);
        Assert.Equal("unknown", missing.Dirty);
    }

    [Fact]
    public void TheFingerprintMovesWhenHeadOrTheIndexChanges()
    {
        Write(Root + "/.git/HEAD", "ref: refs/heads/a\n");
        Write(Root + "/.git/index", "one");
        long before = Probe().Fingerprint(Root);

        Write(Root + "/.git/index", "two-longer");
        long afterIndex = Probe().Fingerprint(Root);
        Write(Root + "/.git/HEAD", "ref: refs/heads/another\n");
        long afterHead = Probe().Fingerprint(Root);

        Assert.NotEqual(0, before);
        Assert.NotEqual(before, afterIndex);
        Assert.NotEqual(afterIndex, afterHead);
    }

    [Fact]
    public void TheFingerprintOfALinkedWorktreeFollowsItsHead()
    {
        string metadata = "/work/main/.git/worktrees/wt";
        Write(Root + "/.git", "gitdir: " + metadata + "\n");
        Write(metadata + "/HEAD", "ref: refs/heads/a\n");
        long before = Probe().Fingerprint(Root);

        Write(metadata + "/HEAD", "ref: refs/heads/longer-branch-name\n");

        Assert.NotEqual(before, Probe().Fingerprint(Root));
    }

    [Fact]
    public void TheFingerprintOfALinkedWorktreeFollowsTheSharedBranchRef()
    {
        string metadata = "/work/main/.git/worktrees/wt";
        string common = Path.GetFullPath("../..", metadata);
        Write(Root + "/.git", "gitdir: " + metadata + "\n");
        Write(metadata + "/HEAD", "ref: refs/heads/topic\n");
        Write(metadata + "/commondir", "../..\n");
        Write(common + "/refs/heads/topic", "0123456789\n");
        long before = Probe().Fingerprint(Root);

        Write(common + "/refs/heads/topic", "fedcba9876543210\n");

        Assert.NotEqual(before, Probe().Fingerprint(Root));
    }

    [Fact]
    public void TheFingerprintOfANormalCheckoutFollowsPackedRefs()
    {
        Write(Root + "/.git/HEAD", "ref: refs/heads/a\n");
        Write(Root + "/.git/packed-refs", "0123456789 refs/heads/a\n");
        long before = Probe().Fingerprint(Root);

        Write(Root + "/.git/packed-refs", "fedcba9876543210 refs/heads/a\n");

        Assert.NotEqual(before, Probe().Fingerprint(Root));
    }

    [Fact]
    public void TheFingerprintOfALinkedWorktreeFollowsItsIndex()
    {
        string metadata = "/work/main/.git/worktrees/wt";
        Write(Root + "/.git", "gitdir: " + metadata + "\n");
        Write(metadata + "/HEAD", "ref: refs/heads/a\n");
        Write(metadata + "/index", "one");
        long before = Probe().Fingerprint(Root);

        Write(metadata + "/index", "two-longer");

        Assert.NotEqual(before, Probe().Fingerprint(Root));
    }
}
