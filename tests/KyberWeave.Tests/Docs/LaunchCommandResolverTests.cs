using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Docs;

/// <summary>Launch-command lookup over an in-memory tree: the search path, the repository root and the PATH the process has.</summary>
public sealed class LaunchCommandResolverTests
{
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "launch"));
    private static readonly string Repo = Path.Combine(Base, "repo");
    private static readonly string BinA = Path.Combine(Base, "binA");
    private static readonly string BinB = Path.Combine(Base, "binB");

    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment;

    public LaunchCommandResolverTests()
    {
        _paths = new FakePathResolver(_files);
        _environment = new FakeProcessEnvironment(currentDirectory: Base);
    }

    [Fact]
    public void ANameIsFoundInTheFirstSearchPathHoldingIt()
    {
        Add(BinA, "serve");
        Add(BinB, "serve");

        Assert.Equal(Path.Combine(BinA, "serve"), ResolverOver(BinA, BinB).Resolve("serve"));
    }

    [Fact]
    public void ANameInALaterSearchPathIsFoundWhenEarlierOnesLackIt()
    {
        Add(BinB, "serve");

        Assert.Equal(Path.Combine(BinB, "serve"), ResolverOver(BinA, BinB).Resolve("serve"));
    }

    [Fact]
    public void ANameInNoSearchPathResolvesToNothing() =>
        Assert.Null(ResolverOver(BinA, BinB).Resolve("serve"));

    [Fact]
    public void ABlankCommandResolvesToNothing() =>
        Assert.Null(ResolverOver(BinA).Resolve("  "));

    [Fact]
    public void AnEmptySearchPathEntryMeansTheRepositoryRoot()
    {
        Add(Repo, "serve");

        Assert.Equal(Path.Combine(Repo, "serve"), ResolverOver(string.Empty).Resolve("serve"));
    }

    [Fact]
    public void ARelativePathCommandIsResolvedAgainstTheRepositoryRoot()
    {
        Add(Path.Combine(Repo, "tools"), "serve");

        string? resolved = ResolverOver().Resolve("." + Path.DirectorySeparatorChar + Path.Combine("tools", "serve"));

        Assert.Equal(Path.Combine(Repo, "tools", "serve"), resolved);
    }

    [Fact]
    public void ARelativePathCommandIsNeverSearchedForOnThePath()
    {
        Add(BinA, "serve");

        Assert.Null(ResolverOver(BinA).Resolve("." + Path.DirectorySeparatorChar + "serve"));
    }

    [Fact]
    public void AFullyQualifiedCommandResolvesOnlyWhenItExists()
    {
        string serve = Path.Combine(BinA, "serve");

        Assert.Null(ResolverOver().Resolve(serve));

        Add(BinA, "serve");

        Assert.Equal(serve, ResolverOver().Resolve(serve));
    }

    [Fact]
    public void TheFactorySearchesThePathTheProcessHas()
    {
        Add(BinB, "serve");
        FakeProcessEnvironment environment = new(
            currentDirectory: Base,
            readVariable: name => name == "PATH" ? BinA + Path.PathSeparator + BinB : null);
        EnvironmentLaunchCommandResolverFactory factory = new(_files, _paths, environment);

        Assert.Equal(Path.Combine(BinB, "serve"), factory.FromEnvironment(Repo).Resolve("serve"));
    }

    [Fact]
    public void TheFactoryResolvesAgainstTheWorkingDirectoryWhenGivenNoRoot()
    {
        Add(Path.Combine(Base, "tools"), "serve");
        EnvironmentLaunchCommandResolverFactory factory = new(_files, _paths, _environment);

        Assert.Equal(
            Path.Combine(Base, "tools", "serve"),
            factory.FromEnvironment().Resolve("." + Path.DirectorySeparatorChar + Path.Combine("tools", "serve")));
    }

    [Fact]
    public void NoPathVariableMeansNoSearchPath()
    {
        Add(BinA, "serve");
        EnvironmentLaunchCommandResolverFactory factory = new(_files, _paths, _environment);

        Assert.Null(factory.FromEnvironment(Repo).Resolve("serve"));
    }

    private LaunchCommandResolver ResolverOver(params string[] searchPaths) =>
        new(Repo, searchPaths, _files, _paths, _environment);

    private void Add(string directory, string name)
    {
        _files.CreateDirectory(directory);
        _files.WriteAllText(Path.Combine(directory, name), string.Empty);
    }
}
