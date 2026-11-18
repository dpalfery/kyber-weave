using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The repository tree probe over the in-memory file system: existence, accessibility and the link-chain walk are
/// proven without a disk or a real symbolic link.
/// </summary>
public sealed class FileRepositoryTreeProbeTests
{
    private static readonly string Root = Path.GetFullPath("/repo");
    private static readonly string Outside = Path.GetFullPath("/elsewhere");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private FileRepositoryTreeProbe Probe() => new(_files);

    private void WriteFile(string path, string text)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, text);
    }

    [Fact]
    public void ProbeRootOfAMissingDirectoryDoesNotExist()
    {
        RepositoryRootAccess access = Probe().ProbeRoot(Root);

        Assert.False(access.Exists);
    }

    [Fact]
    public void ProbeRootOfAListableDirectoryIsAccessible()
    {
        _files.CreateDirectory(Root);

        RepositoryRootAccess access = Probe().ProbeRoot(Root);

        Assert.True(access.Exists);
        Assert.Null(access.AccessError);
    }

    [Fact]
    public void ProbeRootReportsWhyAnExistingDirectoryCannotBeListed()
    {
        _files.CreateDirectory(Root);
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.EnumerateEntries),
            new UnauthorizedAccessException("denied"));

        RepositoryRootAccess access = Probe().ProbeRoot(Root);

        Assert.True(access.Exists);
        Assert.Equal("denied", access.AccessError);
    }

    [Fact]
    public void DirectoryExistsAnswersFromTheSeam()
    {
        _files.CreateDirectory(Path.Combine(Root, "src"));

        Assert.True(Probe().DirectoryExists(Path.Combine(Root, "src")));
        Assert.False(Probe().DirectoryExists(Path.Combine(Root, "docs")));
    }

    [Fact]
    public void AnOrdinaryPathDoesNotLeadOutside()
    {
        WriteFile(Path.Combine(Root, "src", "a.cs"), "x");

        Assert.False(Probe().LeadsOutsideThroughSymlink(Root, "src/a.cs"));
    }

    [Fact]
    public void AMissingPathIsNotALink()
    {
        _files.CreateDirectory(Root);

        Assert.False(Probe().LeadsOutsideThroughSymlink(Root, "nothing/here.cs"));
    }

    [Fact]
    public void ALinkInTheMiddleOfThePathToADirectoryOutsideLeadsOutside()
    {
        WriteFile(Path.Combine(Outside, "secret.txt"), "x");
        _files.CreateDirectoryLink(Path.Combine(Root, "link"), Outside);

        Assert.True(Probe().LeadsOutsideThroughSymlink(Root, "link/secret.txt"));
    }

    [Fact]
    public void ALinkToAFileOutsideLeadsOutside()
    {
        WriteFile(Path.Combine(Outside, "secret.txt"), "x");
        _files.CreateFileLink(Path.Combine(Root, "alias.txt"), Path.Combine(Outside, "secret.txt"));

        Assert.True(Probe().LeadsOutsideThroughSymlink(Root, "alias.txt"));
    }

    [Fact]
    public void ALinkThatStaysInsideTheRootDoesNotLeadOutside()
    {
        WriteFile(Path.Combine(Root, "real", "a.cs"), "x");
        _files.CreateDirectoryLink(Path.Combine(Root, "link"), Path.Combine(Root, "real"));

        Assert.False(Probe().LeadsOutsideThroughSymlink(Root, "link/a.cs"));
    }

    [Fact]
    public void ALinkWhoseTargetCannotBeResolvedIsTreatedAsEscaping()
    {
        WriteFile(Path.Combine(Root, "real", "a.cs"), "x");
        _files.CreateDirectoryLink(Path.Combine(Root, "link"), Path.Combine(Root, "real"));
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.GetFinalLinkTarget),
            new IOException("loop"));

        Assert.True(Probe().LeadsOutsideThroughSymlink(Root, "link/a.cs"));
    }
}
