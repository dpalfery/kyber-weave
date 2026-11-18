using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Mcp;

/// <summary>
/// The containment rule over the path seam: a name belongs to the repository only when it resolves inside the root,
/// and a root given relative resolves against the environment's current directory, never the real process's.
/// </summary>
public sealed class McpRepositoryPathTests
{
    private const string Root = "/work/repo";

    private readonly InMemoryFileSystem _files = new();

    private McpRepositoryPath Path(string currentDirectory = "/kw/work") =>
        new(new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory));

    [Theory]
    [InlineData(".mcp.json")]
    [InlineData(".vscode/mcp.json")]
    [InlineData("..foo/mcp.json")]
    [InlineData(@".vscode\mcp.json")]
    public void ANameInsideTheRepositoryResolvesBelowTheRoot(string name)
    {
        string? full = Path().Contained(Root, name);

        Assert.NotNull(full);
        Assert.StartsWith(Root + "/", full.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside/mcp.json")]
    [InlineData(".vscode/../../outside/mcp.json")]
    [InlineData("/etc/mcp.json")]
    [InlineData("")]
    public void ANameThatLeavesTheRepositoryAnswersNothing(string name)
    {
        Assert.Null(Path().Contained(Root, name));
    }

    [Fact]
    public void ARootTheResolverCannotResolveAnswersNothingInsteadOfThrowing()
    {
        Assert.Null(Path().Contained("\0", ".mcp.json"));
    }

    [Fact]
    public void ARelativeRootResolvesAgainstTheEnvironmentsCurrentDirectory()
    {
        Assert.Equal("/home/dev/repo", Path("/home/dev").FullRoot("repo"));
        Assert.Equal("/home/dev/repo/.mcp.json", Path("/home/dev").Contained("repo", ".mcp.json")?.Replace('\\', '/'));
    }

    [Fact]
    public void ATrailingSeparatorOnTheRootDoesNotChangeTheFileANameResolvesTo()
    {
        Assert.Equal(Path().Contained(Root, ".mcp.json"), Path().Contained(Root + "/", ".mcp.json"));
    }

    [Fact]
    public void ASiblingWhoseNameStartsWithTheRootsIsNotInsideIt()
    {
        Assert.Null(Path().Contained(Root, "../repo-other/mcp.json"));
    }
}
