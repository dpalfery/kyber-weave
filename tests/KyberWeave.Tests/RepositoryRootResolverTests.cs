using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Which repository an MCP process serves, decided from the configuration, path and process-environment seams alone:
/// no disk, no real working directory and no real environment.
/// </summary>
public sealed class RepositoryRootResolverTests
{
    private static readonly string Work = Absolute("kw", "work");
    private static readonly string First = Absolute("kw", "first");
    private static readonly string Second = Absolute("kw", "second");

    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly InMemoryFileSystem _files = new();

    public RepositoryRootResolverTests()
    {
        _configs.WriteRepositoryConfig(First, "stop-on-tool-failure: true\n");
        _configs.WriteRepositoryConfig(Second, "stop-on-tool-failure: true\n");
    }

    [Fact]
    public void InitializedWorkingDirectoryIsTheRoot()
    {
        Assert.Equal(First, Resolver().Resolve([], First, environmentRoot: null));
    }

    [Fact]
    public void UninitializedWorkingDirectoryFailsInsteadOfGuessingAParent()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Resolver().Resolve([], Work, environmentRoot: null));

        Assert.Contains("kyber-weave docs init", error.Message, StringComparison.Ordinal);
        Assert.Contains("will not guess a parent repository", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoRootFlagBeatsTheEnvironmentAndResolvesAgainstTheWorkingDirectory()
    {
        string parent = Path.GetDirectoryName(First)!;

        Assert.Equal(First, Resolver().Resolve(["--repo-root", "first"], parent, environmentRoot: Second));
        Assert.Equal(Second, Resolver().Resolve([], parent, environmentRoot: "second"));
    }

    [Fact]
    public void RelativeWorkingDirectoryIsResolvedAgainstTheProcessWorkingDirectory()
    {
        string parent = Path.GetDirectoryName(First)!;

        Assert.Equal(First, Resolver(currentDirectory: parent).Resolve([], "first", environmentRoot: null));
    }

    [Fact]
    public void ExpectRootNamingAnotherRepositoryIsRefusedAndNamesBothRoots()
    {
        ExpectedRootMismatchException error = Assert.Throws<ExpectedRootMismatchException>(() =>
            Resolver().Resolve(["--repo-root", First, "--expect-root", Second], First, environmentRoot: null));

        Assert.Contains(Second, error.Message, StringComparison.Ordinal);
        Assert.Contains(First, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectRootEqualToTheResolvedRootIsAcceptedWhateverItsSpelling()
    {
        Assert.Equal(First, Resolver().Resolve(["--repo-root", ".", "--expect-root", First + Path.DirectorySeparatorChar], First, null));
        Assert.Equal(First, Resolver().Resolve(["--repo-root", ".", "--expect-root", "."], First, null));
    }

    [Fact]
    public void PathsEqualIsOrdinalSoACaseOnlyDifferenceIsNotEqual()
    {
        RepositoryRootResolver resolver = Resolver();

        Assert.True(resolver.PathsEqual(First, First + Path.DirectorySeparatorChar));
        Assert.False(resolver.PathsEqual(First, Absolute("kw", "FIRST")));
    }

    [Theory]
    [InlineData("--expect-root")]
    [InlineData("--expect-root=")]
    public void ExpectRootFlagWithoutAValueIsAnArgumentError(string flag)
    {
        Assert.Throws<ArgumentException>(() => Resolver().Resolve(["--repo-root", First, flag], First, null));
    }

    private RepositoryRootResolver Resolver(string? currentDirectory = null) =>
        new(_configs, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory ?? Work));

    private static string Absolute(params string[] segments) =>
        Path.GetFullPath(Path.Combine([Path.DirectorySeparatorChar.ToString(), .. segments]));
}
