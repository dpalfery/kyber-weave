using KyberWeave.Arbiter.Hooks;
using Xunit;

namespace KyberWeave.Mcp.Tests;

/// <summary>
/// The registry from a verified MCP client name to the hook's harness token. Each row is a probe's result, so a
/// name no probe saw has no token and the tool writes the prompt file instead of claiming a hook inserts it.
/// </summary>
public sealed class McpClientHarnessTests
{
    [Theory]
    [InlineData("copilot-cli", "copilot-cli")]
    [InlineData("claude-code", "claude")]
    [InlineData("opencode", "opencode")]
    [InlineData("Copilot-CLI", "copilot-cli")]
    public void AVerifiedClientNameMapsToItsHookToken(string clientName, string token) =>
        Assert.Equal(token, McpClientHarness.TokenFor(clientName));

    [Theory]
    [InlineData("copilot")]
    [InlineData("warp")]
    [InlineData("claude")]
    [InlineData(" ")]
    [InlineData(null)]
    public void AnUnknownOrMissingClientNameHasNoToken(string? clientName) =>
        Assert.Null(McpClientHarness.TokenFor(clientName));

    /// <summary>
    /// The token an unverified client is built for must never be one the hook rewrites: under it the service writes the
    /// prompt file the reply names, and that file is the whole delivery on a client no probe has verified.
    /// </summary>
    [Fact]
    public void TheUnverifiedTokenIsNeverARewritingHarness() =>
        Assert.False(DispatchPromptInsertion.CanRewriteDispatch(McpClientHarness.UnverifiedToken));
}
