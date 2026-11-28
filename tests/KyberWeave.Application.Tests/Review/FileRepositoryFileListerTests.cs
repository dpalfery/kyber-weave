using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The repository file lister over the scripted process seam and the in-memory file system: the git listing, the
/// file-system walk it falls back to and the filtering of what it returns are proven without git or a disk.
/// </summary>
public sealed class FileRepositoryFileListerTests
{
    private static readonly string Root = Path.GetFullPath("/repo");

    private readonly ScriptedProcessRunner _git = new();
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private FileRepositoryFileLister Lister() => new(_git, _files);

    private void WriteFile(string relative)
    {
        string path = Path.Combine(Root, relative);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, "x");
    }

    private void ScriptGitListing(string output) =>
        _git.Script(
            "git",
            new ProcessRunResult(0, output, false),
            "ls-files",
            "-z",
            "--cached",
            "--others",
            "--exclude-standard");

    [Fact]
    public async Task ListThrowsWhenTheRootDoesNotExist()
    {
        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Lister().ListAsync(Root, CancellationToken.None));
    }

    [Fact]
    public async Task ListRejectsABlankRoot()
    {
        _ = await Assert.ThrowsAsync<ArgumentException>(() => Lister().ListAsync(" ", CancellationToken.None));
    }

    [Fact]
    public async Task ListAsksGitForTrackedAndUntrackedFilesInTheRootWithinASixtySecondLimit()
    {
        _files.CreateDirectory(Root);
        WriteFile("src/a.cs");
        ScriptGitListing("src/a.cs\0");

        _ = await Lister().ListAsync(Root, CancellationToken.None);

        ProcessRunRequest call = Assert.Single(_git.Calls);
        Assert.Equal(Root, call.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);
    }

    [Fact]
    public async Task ListReturnsGitsFilesThatStillExistAndLeavesOutExcludedDirectories()
    {
        WriteFile("src/a.cs");
        WriteFile("README.md");
        WriteFile("node_modules/pkg/index.js");
        ScriptGitListing("src/a.cs\0README.md\0node_modules/pkg/index.js\0gone.cs\0");

        IReadOnlyList<string> files = await Lister().ListAsync(Root, CancellationToken.None);

        Assert.Equal(["src/a.cs", "README.md"], files);
    }

    [Fact]
    public async Task ListLeavesOutAGitFileThatIsALink()
    {
        WriteFile("real.cs");
        _files.CreateFileLink(Path.Combine(Root, "alias.cs"), Path.Combine(Root, "real.cs"));
        ScriptGitListing("real.cs\0alias.cs\0");

        IReadOnlyList<string> files = await Lister().ListAsync(Root, CancellationToken.None);

        Assert.Equal(["real.cs"], files);
    }

    [Fact]
    public async Task ListWalksTheFileSystemWhenGitIsNotInstalled()
    {
        WriteFile("src/a.cs");
        WriteFile("top.sln");
        _ = _git.WithMissingProgram("git");

        IReadOnlyList<string> files = await Lister().ListAsync(Root, CancellationToken.None);

        Assert.Equal(["src/a.cs", "top.sln"], files.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListWalksTheFileSystemWhenTheDirectoryIsNotAWorkTree()
    {
        WriteFile("a.cs");
        _ = _git.Script("git", new ProcessRunResult(128, string.Empty, false), "ls-files", "-z", "--cached", "--others", "--exclude-standard");

        IReadOnlyList<string> files = await Lister().ListAsync(Root, CancellationToken.None);

        Assert.Equal(["a.cs"], files);
    }

    [Fact]
    public async Task TheWalkNeverEntersAnExcludedDirectoryOrFollowsALink()
    {
        WriteFile("src/a.cs");
        WriteFile("node_modules/pkg/index.js");
        WriteFile("bin/out.dll");
        WriteFile("../elsewhere/secret.txt");
        _files.CreateDirectoryLink(Path.Combine(Root, "linked"), Path.GetFullPath("/elsewhere"));
        _files.CreateFileLink(Path.Combine(Root, "alias.cs"), Path.Combine(Root, "src", "a.cs"));
        _ = _git.WithMissingProgram("git");

        IReadOnlyList<string> files = await Lister().ListAsync(Root, CancellationToken.None);

        Assert.Equal(["src/a.cs"], files);
    }

    [Fact]
    public async Task TheWalkSkipsADirectoryTheFileSystemCannotList()
    {
        WriteFile("src/a.cs");
        WriteFile("locked/b.cs");
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.EnumerateEntries)
                && operation.Path is { } path
                && path.EndsWith("locked", StringComparison.Ordinal),
            new UnauthorizedAccessException("denied"));
        _ = _git.WithMissingProgram("git");

        IReadOnlyList<string> files = await Lister().ListAsync(Root, CancellationToken.None);

        Assert.Equal(["src/a.cs"], files);
    }

    [Fact]
    public async Task ListAsyncThrowsWhenTheRootDoesNotExist()
    {
        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Lister().ListAsync(Root, CancellationToken.None));
    }

    [Fact]
    public async Task ListAsyncReturnsGitsFilesThatStillExistAndLeavesOutExcludedDirectories()
    {
        WriteFile("src/a.cs");
        WriteFile("README.md");
        WriteFile("node_modules/pkg/index.js");
        ScriptGitListing("src/a.cs\0README.md\0node_modules/pkg/index.js\0gone.cs\0");

        IReadOnlyList<string> files = await Lister().ListAsync(Root, CancellationToken.None);

        Assert.Equal(["src/a.cs", "README.md"], files);
    }

    [Fact]
    public async Task ListAsyncWalksTheFileSystemWhenGitIsNotInstalled()
    {
        WriteFile("src/a.cs");
        WriteFile("top.sln");
        _ = _git.WithMissingProgram("git");

        IReadOnlyList<string> files = await Lister().ListAsync(Root, CancellationToken.None);

        Assert.Equal(["src/a.cs", "top.sln"], files.Order(StringComparer.Ordinal));
    }
}
