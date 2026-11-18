using KyberWeave.Arbiter.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The serve root resolver over the file-system, path and environment seams: the flag wins, then the environment
/// variable, then the working directory, and a relative value resolves against the working directory the process
/// seam names rather than the one the test runner happens to have.
/// </summary>
public sealed class ArbiterRootResolverTests
{
    private const string WorkingDirectory = "/kw/work";

    private readonly InMemoryFileSystem _files = new();

    private ArbiterRootResolver Resolver() =>
        new(
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(
                currentDirectory: WorkingDirectory,
                isFileSystemCaseInsensitive: !_files.CaseSensitive));

    [Fact]
    public void TheWorkingDirectoryIsTheRootWhenNothingNamesOne()
    {
        _files.CreateDirectory(WorkingDirectory);

        Assert.Equal(WorkingDirectory, Resolver().Resolve([], WorkingDirectory, null));
    }

    [Fact]
    public void ARelativeFlagResolvesAgainstTheWorkingDirectory()
    {
        _files.CreateDirectory("/kw/work/checkout");

        Assert.Equal("/kw/work/checkout", Resolver().Resolve(["--repo-root", "checkout"], WorkingDirectory, null));
    }

    [Fact]
    public void ARelativeEnvironmentRootResolvesAgainstTheWorkingDirectory()
    {
        _files.CreateDirectory("/kw/work/checkout");

        Assert.Equal("/kw/work/checkout", Resolver().Resolve([], WorkingDirectory, "checkout"));
    }

    [Fact]
    public void TheFlagWinsOverTheEnvironmentVariable()
    {
        _files.CreateDirectory("/kw/work/flagged");
        _files.CreateDirectory("/kw/work/from-environment");

        Assert.Equal(
            "/kw/work/flagged",
            Resolver().Resolve(["--repo-root=flagged"], WorkingDirectory, "from-environment"));
    }

    [Fact]
    public void ARootThatIsNotADirectoryIsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => Resolver().Resolve(["--repo-root", "missing"], WorkingDirectory, null));
    }

    [Fact]
    public void AFlagWithoutAPathIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Resolver().Resolve(["--repo-root"], WorkingDirectory, null));
    }
}
