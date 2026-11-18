using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Proves the worktree MCP canonical binding helper — argument splicing and the <c>.codegraph</c> link ban — through the
/// in-memory file system, links included.
/// </summary>
public sealed class SquadWorktreeMcpTests
{
    private static readonly string Base = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "worktree-mcp");
    private static readonly string Canonical = Path.Combine(Base, "canonical", "repo");
    private static readonly string Outside = Path.Combine(Base, "outside");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private SquadWorktreeMcp Mcp =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: Base));

    public SquadWorktreeMcpTests()
    {
        _files.CreateDirectory(Canonical);
        _files.CreateDirectory(Outside);
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    [Fact]
    public void RepoRootAndPathValuesBecomeAbsoluteCanonicalPaths()
    {
        IReadOnlyList<string> spliced = Mcp.SpliceArguments(
            ["--repo-root", ".", "--path", "./subpath", "--other", "val"], Canonical);

        Assert.Equal(Canonical, spliced[IndexOf(spliced, "--repo-root") + 1]);
        Assert.Equal(Path.Combine(Canonical, "subpath"), spliced[IndexOf(spliced, "--path") + 1]);
        Assert.Equal("val", spliced[IndexOf(spliced, "--other") + 1]);
    }

    [Fact]
    public void TheEqualsFormsAreSplicedToo()
    {
        IReadOnlyList<string> spliced = Mcp.SpliceArguments(["--repo-root=.", "--path=sub"], Canonical);

        Assert.Equal([$"--repo-root={Canonical}", $"--path={Path.Combine(Canonical, "sub")}"], spliced);
    }

    [Fact]
    public void APathThatLeavesTheCanonicalRootIsRefusedWhileOneInsideIsKept()
    {
        Assert.Equal(
            ["--path", Path.Combine(Canonical, "inside")],
            Mcp.SpliceArguments(["--path", "inside"], Canonical));

        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments(["--path", "../../../etc"], Canonical));
    }

    [Fact]
    public void ARelativeCanonicalRootFailsClosedWhileAnAbsoluteOneIsAccepted()
    {
        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments(["--repo-root", "."], "relative/canonical/root"));

        Assert.NotEmpty(Mcp.SpliceArguments(["--repo-root", "."], Canonical));
    }

    [Theory]
    [InlineData("--repo-root")]
    [InlineData("--path")]
    public void ATrailingFlagWithoutAValueFailsClosed(string flag)
    {
        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments([flag], Canonical));
    }

    [Theory]
    [InlineData("--repo-root")]
    [InlineData("--path")]
    public void AFlagFollowedByAnotherFlagFailsClosed(string flag)
    {
        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments([flag, "--other", "x"], Canonical));
    }

    [Fact]
    public void ANullArgumentIsAnArgumentErrorNotANullReference()
    {
        string[] args = ["--repo-root", ".", null!];

        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments(args, Canonical));
    }

    [Fact]
    public void AnEmptyPathValueReportsTheMissingValueNotATraversal()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments(["--path", ""], Canonical));

        Assert.Contains("--path requires a non-empty value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkInsideTheRootThatLeadsOutsideIsRefused()
    {
        _files.CreateDirectoryLink(Path.Combine(Canonical, "escape"), Outside);

        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments(["--path", "escape"], Canonical));
    }

    [Fact]
    public void ALinkInsideTheRootThatLeadsToAnotherPlaceInsideIsFollowed()
    {
        string real = Path.Combine(Canonical, "real");
        _files.CreateDirectory(real);
        _files.CreateDirectoryLink(Path.Combine(Canonical, "alias"), real);

        Assert.Equal(["--path", real], Mcp.SpliceArguments(["--path", "alias"], Canonical));
    }

    [Fact]
    public void ARelativeLinkTargetResolvesAgainstTheLinksDirectory()
    {
        string real = Path.Combine(Canonical, "real");
        _files.CreateDirectory(real);
        _files.CreateDirectoryLink(Path.Combine(Canonical, "alias"), "real");

        Assert.Equal(["--path", real], Mcp.SpliceArguments(["--path", "alias"], Canonical));
    }

    [Fact]
    public void ADanglingLinkResolvesToItsMissingTargetRatherThanFailing()
    {
        string missingInside = Path.Combine(Canonical, "not-there-yet");
        _files.CreateDirectoryLink(Path.Combine(Canonical, "dangling-in"), missingInside);
        _files.CreateDirectoryLink(Path.Combine(Canonical, "dangling-out"), Path.Combine(Outside, "not-there-yet"));

        Assert.Equal(["--path", missingInside], Mcp.SpliceArguments(["--path", "dangling-in"], Canonical));
        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments(["--path", "dangling-out"], Canonical));
    }

    [Fact]
    public void ALinkWhoseTargetPassesThroughAnotherLinkThatLeavesTheRootIsRefused()
    {
        _files.CreateDirectoryLink(Path.Combine(Canonical, "sublink"), Outside);
        _files.CreateDirectoryLink(Path.Combine(Canonical, "via"), Path.Combine(Canonical, "sublink", "x"));

        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments(["--path", "via"], Canonical));
    }

    [Fact]
    public void ALinkWhoseTargetPassesThroughAnotherLinkThatStaysInsideIsResolvedFully()
    {
        string real = Path.Combine(Canonical, "real");
        _files.CreateDirectory(Path.Combine(real, "x"));
        _files.CreateDirectoryLink(Path.Combine(Canonical, "sublink"), real);
        _files.CreateDirectoryLink(Path.Combine(Canonical, "via"), Path.Combine(Canonical, "sublink", "x"));

        Assert.Equal(["--path", Path.Combine(real, "x")], Mcp.SpliceArguments(["--path", "via"], Canonical));
    }

    [Fact]
    public void ALinkThatCannotBeResolvedIsAnArgumentErrorNotALeakedPlatformError()
    {
        string loop = Path.Combine(Canonical, "loop");
        _files.CreateDirectoryLink(loop, loop);

        Assert.Throws<ArgumentException>(() => Mcp.SpliceArguments(["--path", "loop"], Canonical));
    }

    [Fact]
    public void AMissingWorktreeDirectoryIsReported()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Mcp.EnsureNoCodegraphSymlink(Path.Combine(Base, "missing")));
    }

    [Fact]
    public void ACodegraphDirectoryOrNoneAtAllIsAccepted()
    {
        Mcp.EnsureNoCodegraphSymlink(Canonical);

        _files.CreateDirectory(Path.Combine(Canonical, ".codegraph"));

        Mcp.EnsureNoCodegraphSymlink(Canonical);
    }

    [Fact]
    public void ACodegraphLinkIsRefusedNamingTheLink()
    {
        _files.CreateDirectoryLink(Path.Combine(Canonical, ".codegraph"), Outside);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => Mcp.EnsureNoCodegraphSymlink(Canonical));

        Assert.Contains(".codegraph", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACodegraphFileLinkIsRefusedToo()
    {
        _files.WriteAllText(Path.Combine(Outside, "index"), "x");
        _files.CreateFileLink(Path.Combine(Canonical, ".codegraph"), Path.Combine(Outside, "index"));

        Assert.Throws<InvalidOperationException>(() => Mcp.EnsureNoCodegraphSymlink(Canonical));
    }
}
