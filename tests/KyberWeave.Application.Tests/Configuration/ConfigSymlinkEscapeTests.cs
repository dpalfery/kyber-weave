using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Configuration;

/// <summary>
/// A gate's <c>working-directory</c> that reaches outside the repository through a symbolic link is refused when the
/// configuration is read, and the shared config fake states that escape so the refusal is provable without a link on
/// disk.
/// </summary>
/// <remarks>
/// The adapter answers this from a real link probe. Without the same answer the fake loads a configuration the disk
/// refuses, so a suite over the fake would pass a gate that escapes the repository — fake drift on a containment rule.
/// </remarks>
public sealed class ConfigSymlinkEscapeTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly string _root;

    public ConfigSymlinkEscapeTests() => _root = _files.CreateTemporaryDirectory("kw-config-symlink-");

    [Fact]
    public void AWorkingDirectoryTheTreeSaysEscapesThroughALinkIsRefused()
    {
        _configs.CreateDirectory(Path.Combine(_root, "web"));
        _configs.WriteRepositoryConfig(_root, """
            review:
              gates:
                - id: web-build
                  run: [npm, run, build]
                  working-directory: web
            """);
        _configs.EscapeThroughSymlink("web");

        KyberWeaveConfigLoadResult result = _configs.TryLoad(_root);

        Assert.False(result.Success);
        Assert.Contains("symbolic link", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameDirectoryLoadsWhenTheLinkLeadsInsideTheRepository()
    {
        _configs.CreateDirectory(Path.Combine(_root, "web"));
        _configs.WriteRepositoryConfig(_root, """
            review:
              gates:
                - id: web-build
                  run: [npm, run, build]
                  working-directory: web
            """);

        KyberWeaveConfigLoadResult result = _configs.TryLoad(_root);

        Assert.True(result.Success, result.Error);
        Assert.Equal("web", Assert.Single(result.Config!.Review.Gates).WorkingDirectory);
    }
}
