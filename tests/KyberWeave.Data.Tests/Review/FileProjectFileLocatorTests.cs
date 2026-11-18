using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Review;

/// <summary>The nearest project file for a changed file, found over an in-memory tree.</summary>
public sealed class FileProjectFileLocatorTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(new InMemoryFileSystem().RootPath, "repo"));

    private readonly InMemoryFileSystem _files = new();
    private readonly FileProjectFileLocator _locator;

    public FileProjectFileLocatorTests() =>
        _locator = new FileProjectFileLocator(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: Root));

    [Fact]
    public void TheProjectFileInTheSameFolderIsFound()
    {
        Add("src/App/App.csproj");
        Add("src/App/Program.cs");

        Assert.Equal("src/App/App.csproj", _locator.NearestProjectFile(Root, "src/App/Program.cs"));
    }

    [Fact]
    public void ThePathClimbsToTheNearestAncestorWithAProjectFile()
    {
        Add("src/App/App.csproj");
        Add("src/App/Deep/Er/Thing.cs");

        Assert.Equal("src/App/App.csproj", _locator.NearestProjectFile(Root, "src/App/Deep/Er/Thing.cs"));
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("pyproject.toml")]
    [InlineData("Cargo.toml")]
    [InlineData("go.mod")]
    [InlineData("Lib.fsproj")]
    public void OtherProjectMarkersCount(string marker)
    {
        Add("pkg/" + marker);
        Add("pkg/source.txt");

        Assert.Equal("pkg/" + marker, _locator.NearestProjectFile(Root, "pkg/source.txt"));
    }

    [Fact]
    public void NoProjectFileAnywhereFindsNothing()
    {
        Add("docs/readme.md");

        Assert.Null(_locator.NearestProjectFile(Root, "docs/readme.md"));
    }

    [Fact]
    public void ABlankRepositoryRootFindsNothing() =>
        Assert.Null(_locator.NearestProjectFile(" ", "src/App/Program.cs"));

    [Fact]
    public void ARootSpelledWithATrailingSeparatorDoesNotSearchTheRootItself()
    {
        Add("package.json");
        Add("a.cs");

        Assert.Null(_locator.NearestProjectFile(Root + Path.DirectorySeparatorChar, "a.cs"));
    }

    [Fact]
    public void ARootSpelledWithoutATrailingSeparatorSearchesTheRootItself()
    {
        Add("package.json");
        Add("a.cs");

        Assert.Equal("package.json", _locator.NearestProjectFile(Root, "a.cs"));
    }

    private void Add(string relativePath)
    {
        string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, string.Empty);
    }
}
