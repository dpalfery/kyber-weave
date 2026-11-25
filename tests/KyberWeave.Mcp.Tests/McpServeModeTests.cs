using Xunit;

namespace KyberWeave.Mcp.Tests;

/// <summary>
/// Which mode the docs server starts in, read from its argument list alone: bound to one root, or serving any
/// initialized repository a call names, and the flags that cannot be combined with any-repo mode.
/// </summary>
public sealed class McpServeModeTests
{
    [Fact]
    public void NoFlagIsBoundMode()
    {
        McpServeMode mode = McpServeMode.Parse(["--repo-root", "."]);

        Assert.False(mode.AnyRepo);
        Assert.Null(mode.Conflict);
        Assert.Null(mode.ConflictDiagnostic);
    }

    [Fact]
    public void AnEmptyArgumentListIsBoundMode()
    {
        McpServeMode mode = McpServeMode.Parse([]);

        Assert.False(mode.AnyRepo);
        Assert.Null(mode.Conflict);
    }

    [Theory]
    [InlineData("--any-repo")]
    [InlineData("--any-repo", "--no-codegraph-refresh")]
    public void AnyRepoFlagIsAnyRepoMode(params string[] args)
    {
        McpServeMode mode = McpServeMode.Parse(args);

        Assert.True(mode.AnyRepo);
        Assert.Null(mode.Conflict);
    }

    [Theory]
    [InlineData("--repo-root", "--any-repo", "--repo-root", ".")]
    [InlineData("--repo-root", "--repo-root=.", "--any-repo")]
    [InlineData("--expect-root", "--any-repo", "--expect-root", "/somewhere")]
    [InlineData("--expect-root", "--expect-root=/somewhere", "--any-repo")]
    public void AnyRepoWithARootFlagIsAConflictNamingItsRuleId(string rootFlag, params string[] args)
    {
        McpServeMode mode = McpServeMode.Parse(args);

        Assert.NotNull(mode.Conflict);
        Assert.Contains("--any-repo", mode.Conflict, StringComparison.Ordinal);
        Assert.Contains("combined with " + rootFlag + ":", mode.Conflict, StringComparison.Ordinal);
        Assert.Contains("Remove " + rootFlag + " ", mode.Conflict, StringComparison.Ordinal);
        Assert.Equal("KW-MCP-ROOT-003", McpServeMode.ConflictRuleId);
        Assert.StartsWith("KW-MCP-ROOT-003: ", mode.ConflictDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootFlagWithoutAnyRepoIsNoConflict()
    {
        McpServeMode mode = McpServeMode.Parse(["--repo-root", ".", "--expect-root", "."]);

        Assert.False(mode.AnyRepo);
        Assert.Null(mode.Conflict);
    }
}
