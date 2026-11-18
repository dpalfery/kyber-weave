using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The test-class source reader over the in-memory file system and the fake process environment: the walk, the
/// repository-root search and the namespace read are proven without a disk.
/// </summary>
public sealed class FileTestClassSourcesTests
{
    private static readonly string Root = Path.GetFullPath("/repo");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private FileTestClassSources Sources(string? currentDirectory = null, string? baseDirectory = null) =>
        new(_files,
            new FakeProcessEnvironment(
                currentDirectory: currentDirectory ?? Path.GetFullPath("/nowhere"),
                applicationBaseDirectory: baseDirectory ?? Path.GetFullPath("/nowhere/app")),
            new FakePathResolver(_files));

    private void WriteFile(string relative, string contents = "x")
    {
        string path = Path.Combine(Root, relative);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, contents);
    }

    [Fact]
    public void OpenListsEverySourceFileByItsNameWithoutTheExtension()
    {
        WriteFile("src/Alpha.cs");
        WriteFile("tests/deep/Beta.cs");
        WriteFile("src/readme.md");

        TestSourceTree? tree = Sources().Open(Root);

        Assert.NotNull(tree);
        Assert.Equal(Root, tree.Repository);
        Assert.Equal(["Alpha", "Beta"], tree.SourcesByName.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Path.Combine(Root, "src", "Alpha.cs"), tree.SourcesByName["Alpha"]);
    }

    [Fact]
    public void OpenNeverEntersAGeneratedDirectory()
    {
        WriteFile("src/Kept.cs");
        WriteFile("obj/Generated.cs");
        WriteFile("BIN/Generated2.cs");

        TestSourceTree? tree = Sources().Open(Root);

        Assert.NotNull(tree);
        Assert.Equal(["Kept"], tree.SourcesByName.Keys);
    }

    [Fact]
    public void OpenSkipsALinkAndNeverFollowsIt()
    {
        WriteFile("src/Real.cs");
        _files.CreateFileLink(Path.Combine(Root, "src", "Linked.cs"), Path.Combine(Root, "src", "Real.cs"));
        string outside = Path.GetFullPath("/outside");
        _files.CreateDirectory(outside);
        _files.WriteAllText(Path.Combine(outside, "Escaped.cs"), "x");
        _files.CreateDirectoryLink(Path.Combine(Root, "src", "out"), outside);

        TestSourceTree? tree = Sources().Open(Root);

        Assert.NotNull(tree);
        Assert.Equal(["Real"], tree.SourcesByName.Keys);
    }

    [Fact]
    public void OpenSkipsADirectoryTheWalkCannotReadAndKeepsTheRest()
    {
        WriteFile("locked/Hidden.cs");
        WriteFile("open/Seen.cs");
        string locked = Path.Combine(Root, "locked");
        _files.FailWhen(
            operation => operation.Name == nameof(IFileSystem.EnumerateEntries) && operation.Path == locked,
            new UnauthorizedAccessException("denied"));

        TestSourceTree? tree = Sources().Open(Root);

        Assert.NotNull(tree);
        Assert.Equal(["Seen"], tree.SourcesByName.Keys);
    }

    [Fact]
    public void OpenAnswersNullWhenTheRootIsNotADirectory()
    {
        Assert.Null(Sources().Open(Path.Combine(Root, "missing")));
        Assert.Null(Sources().Open(" "));
    }

    [Fact]
    public void OpenThrowsWhenTheRootExistsButCannotBeRead()
    {
        WriteFile("src/A.cs");
        _files.FailWhen(
            operation => operation.Name == nameof(IFileSystem.EnumerateEntries) && operation.Path == Root,
            new UnauthorizedAccessException("denied"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Sources().Open(Root));

        Assert.Contains(Root, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenWithoutARootUsesTheRepositoryAboveTheWorkingDirectory()
    {
        WriteFile("src/Found.cs");
        _files.CreateDirectory(Path.Combine(Root, ".git"));
        string nested = Path.Combine(Root, "src", "deeper");
        _files.CreateDirectory(nested);

        TestSourceTree? tree = Sources(currentDirectory: nested).Open(null);

        Assert.NotNull(tree);
        Assert.Equal(Root, tree.Repository);
        Assert.Contains("Found", tree.SourcesByName.Keys);
    }

    [Fact]
    public void OpenReadsARelativeRootAgainstTheWorkingDirectory()
    {
        _files.CreateDirectory(Path.Combine("/work", "repo", "src"));
        _files.WriteAllText(Path.Combine("/work", "repo", "src", "Alpha.cs"), "x");
        FileTestClassSources sources = new(
            _files,
            new FakeProcessEnvironment(currentDirectory: "/work"),
            new FakePathResolver(_files));

        TestSourceTree? tree = sources.Open("repo");

        Assert.NotNull(tree);
        Assert.Equal(Path.Combine("/work", "repo"), tree.Repository);
    }

    [Fact]
    public void OpenWithoutARootRecognisesAWorktreeWhoseGitIsAFileAndAKyberWeaveFolder()
    {
        WriteFile(".git", "gitdir: elsewhere");
        Assert.Equal(Root, Sources(currentDirectory: Root).Open(null)!.Repository);

        InMemoryFileSystem other = new(caseSensitive: true);
        other.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        FileTestClassSources sources = new(other, new FakeProcessEnvironment(currentDirectory: Root), new FakePathResolver(other));
        Assert.Equal(Root, sources.Open(null)!.Repository);
    }

    [Fact]
    public void OpenWithoutARootFallsBackToTheProgramsOwnDirectoryWhenTheWorkingDirectoryIsOutsideAnyRepository()
    {
        WriteFile("Program.cs");
        _files.CreateDirectory(Path.Combine(Root, ".git"));
        string baseDirectory = Path.Combine(Root, "bin");
        _files.CreateDirectory(baseDirectory);

        TestSourceTree? tree = Sources(baseDirectory: baseDirectory).Open(null);

        Assert.NotNull(tree);
        Assert.Equal(Root, tree.Repository);
    }

    [Fact]
    public void OpenWithoutARootAndNoRepositoryAnywhereWalksTheWorkingDirectory()
    {
        string work = Path.GetFullPath("/loose");
        _files.CreateDirectory(work);
        _files.WriteAllText(Path.Combine(work, "Loose.cs"), "x");

        TestSourceTree? tree = Sources(currentDirectory: work).Open(null);

        Assert.NotNull(tree);
        Assert.Equal(work, tree.Repository);
        Assert.Equal(["Loose"], tree.SourcesByName.Keys);
    }

    [Fact]
    public void ReadDeclaredNamespaceAnswersTheNamespaceTheFileDeclares()
    {
        WriteFile("A.cs", "// header\nnamespace My.Tests;\npublic class A {}");

        Assert.Equal("My.Tests", Sources().ReadDeclaredNamespace(Path.Combine(Root, "A.cs")));
    }

    [Fact]
    public void ReadDeclaredNamespaceAnswersNullWhenTheFileIsMissingOrUnreadable()
    {
        WriteFile("B.cs", "namespace X;");
        string unreadable = Path.Combine(Root, "B.cs");
        _files.FailWhen(operation => operation.Name == nameof(IFileSystem.ReadLines), new IOException("locked"));

        Assert.Null(Sources().ReadDeclaredNamespace(unreadable));
        Assert.Null(Sources().ReadDeclaredNamespace(Path.Combine(Root, "none.cs")));
    }
}
