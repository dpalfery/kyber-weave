using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Integration.Tests.Squad;

/// <summary>
/// Proves on the real disk what the in-memory worktree MCP tests prove with fake links: a worktree's
/// <c>.codegraph</c> link and a <c>--path</c> link out of the canonical root are refused, and a link that loops or
/// dangles behaves as the platform resolves it.
/// </summary>
public sealed class SquadWorktreeMcpLinkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string MakeDirectory(string name)
    {
        string path = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>The canonical root as the helper sees it, with the platform's own links in the temp path resolved.</summary>
    private static string RealRoot(string canonical) =>
        RealSquadServices.WorktreeMcp().SpliceArguments(["--repo-root", "."], canonical)[1];

    [Fact]
    public void ACodegraphDirectoryIsAcceptedAndALinkToOneIsRefused()
    {
        string worktree = MakeDirectory("worktree");
        string target = MakeDirectory("target");
        Directory.CreateDirectory(Path.Combine(worktree, ".codegraph"));
        RealSquadServices.WorktreeMcp().EnsureNoCodegraphSymlink(worktree);

        string linked = MakeDirectory("linked-worktree");
        Directory.CreateSymbolicLink(Path.Combine(linked, ".codegraph"), target);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => RealSquadServices.WorktreeMcp().EnsureNoCodegraphSymlink(linked));
        Assert.Contains(".codegraph", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADanglingCodegraphLinkIsRefused()
    {
        string worktree = MakeDirectory("worktree");
        Directory.CreateSymbolicLink(Path.Combine(worktree, ".codegraph"), Path.Combine(_temp.Path, "gone"));

        Assert.Throws<InvalidOperationException>(() => RealSquadServices.WorktreeMcp().EnsureNoCodegraphSymlink(worktree));
    }

    [Fact]
    public void APathLinkLeadingOutsideTheCanonicalRootIsRefusedAndOneInsideIsFollowed()
    {
        string canonical = MakeDirectory("canonical");
        string outside = MakeDirectory("outside");
        MakeDirectory(Path.Combine("canonical", "real"));
        Directory.CreateSymbolicLink(Path.Combine(canonical, "escape"), outside);
        Directory.CreateSymbolicLink(Path.Combine(canonical, "alias"), Path.Combine(RealRoot(canonical), "real"));

        Assert.Throws<ArgumentException>(
            () => RealSquadServices.WorktreeMcp().SpliceArguments(["--path", "escape"], canonical));
        Assert.Equal(
            ["--path", Path.Combine(RealRoot(canonical), "real")],
            RealSquadServices.WorktreeMcp().SpliceArguments(["--path", "alias"], canonical));
    }

    [Fact]
    public void ALinkThatLoopsOnItselfIsAnArgumentError()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Self-referential symlinks are not created on Windows without developer mode.");
        string canonical = MakeDirectory("canonical");
        string loop = Path.Combine(canonical, "loop");
        Directory.CreateSymbolicLink(loop, loop);

        Assert.Throws<ArgumentException>(
            () => RealSquadServices.WorktreeMcp().SpliceArguments(["--path", "loop"], canonical));
    }

    [Fact]
    public void ADanglingPathLinkResolvesToItsMissingTargetWhenInsideTheRootAndIsRefusedWhenOutside()
    {
        string canonical = MakeDirectory("canonical");
        string missingInside = Path.Combine(RealRoot(canonical), "not-there-yet");
        Directory.CreateSymbolicLink(Path.Combine(canonical, "dangling-in"), missingInside);
        Directory.CreateSymbolicLink(Path.Combine(canonical, "dangling-out"), Path.Combine(_temp.Path, "gone"));

        Assert.Equal(
            ["--path", missingInside],
            RealSquadServices.WorktreeMcp().SpliceArguments(["--path", "dangling-in"], canonical));
        Assert.Throws<ArgumentException>(
            () => RealSquadServices.WorktreeMcp().SpliceArguments(["--path", "dangling-out"], canonical));
    }
}
