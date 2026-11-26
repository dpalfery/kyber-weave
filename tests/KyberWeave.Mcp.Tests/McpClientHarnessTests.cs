using KyberWeave.Arbiter.Hooks;
using Xunit;

namespace KyberWeave.Mcp.Tests;

/// <summary>
/// The registry from a verified MCP client name to the hook's harness token. Each row rests on a live probe, the
/// client's own logs or its own source, so a name none of them showed has no token and the tool writes the prompt file instead of claiming a hook inserts it.
/// </summary>
public sealed class McpClientHarnessTests
{
    [Theory]
    [InlineData("copilot-cli", "copilot-cli")]
    [InlineData("claude-code", "claude")]
    [InlineData("opencode", "opencode")]
    [InlineData("factory-cli", "factory")]
    [InlineData("Cursor", "cursor")]
    [InlineData("kilo", "kilo")]
    [InlineData("antigravity-client", "antigravity")]
    [InlineData("Visual Studio Code", "copilot-vscode")]
    [InlineData("Visual Studio Code - Insiders", "copilot-vscode")]
    [InlineData("codex-mcp-client", "codex")]
    [InlineData("pi-mcp-kw-docs", "pi")]
    [InlineData("pi-mcp-kyber-weave", "pi")]
    [InlineData("Copilot-CLI", "copilot-cli")]
    public void AVerifiedClientNameMapsToItsHookToken(string clientName, string token) =>
        Assert.Equal(token, McpClientHarness.TokenFor(clientName));

    [Theory]
    [InlineData("copilot")]
    [InlineData("warp")]
    [InlineData("claude")]
    [InlineData("pi-mcp-")]
    [InlineData(" ")]
    [InlineData(null)]
    public void AnUnknownOrMissingClientNameHasNoToken(string? clientName) =>
        Assert.Null(McpClientHarness.TokenFor(clientName));

    /// <summary>Every verified name maps to a harness whose hook rewrites, or the name would buy nothing.</summary>
    [Theory]
    [InlineData("factory-cli")]
    [InlineData("Cursor")]
    [InlineData("kilo")]
    [InlineData("antigravity-client")]
    [InlineData("Visual Studio Code")]
    [InlineData("Visual Studio Code - Insiders")]
    [InlineData("codex-mcp-client")]
    [InlineData("pi-mcp-kw-docs")]
    [InlineData("copilot-cli")]
    [InlineData("claude-code")]
    [InlineData("opencode")]
    public void AVerifiedClientMapsToARewritingHarness(string clientName) =>
        Assert.True(DispatchPromptInsertion.CanRewriteDispatch(McpClientHarness.TokenFor(clientName)));

    /// <summary>
    /// The token an unverified client is built for must never be one the hook rewrites: under it the service writes the
    /// prompt file the reply names, and that file is the whole delivery on a client no probe, log or source has verified.
    /// </summary>
    [Fact]
    public void TheUnverifiedTokenIsNeverARewritingHarness() =>
        Assert.False(DispatchPromptInsertion.CanRewriteDispatch(McpClientHarness.UnverifiedToken));
}
