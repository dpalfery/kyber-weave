using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Docs.Mcp;
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

    /// <summary>Each registry row's client names resolve to that row's token, prefix rule included.</summary>
    [Fact]
    public void EveryRegistryClientNameMapsToItsHarnessToken()
    {
        foreach (HarnessDescriptor row in HarnessRegistry.Rows)
        {
            foreach (string name in row.McpClientNames)
            {
                Assert.Equal(row.Token, McpClientHarness.TokenFor(name));
            }

            if (row.McpClientPrefix is { } prefix)
            {
                Assert.Equal(row.Token, McpClientHarness.TokenFor(prefix + "any-server-key"));
            }
        }
    }

    /// <summary>Every token the mapper can return is a registry row, so no name buys a token the registry does not hold.</summary>
    [Fact]
    public void EveryTokenTheMapperReturnsIsARegistryHarness()
    {
        foreach (HarnessDescriptor row in HarnessRegistry.Rows)
        {
            foreach (string name in row.McpClientNames)
            {
                string? token = McpClientHarness.TokenFor(name);
                Assert.NotNull(token);
                Assert.NotNull(HarnessRegistry.ByToken(token));
            }
        }

        Assert.Null(HarnessRegistry.ByToken(McpClientHarness.UnverifiedToken));
    }

    /// <summary>
    /// Devin rewrites its dispatch but has no verified client name, so its dispatches take the prompt file. The empty
    /// list is the deliberate decision, not a missing row.
    /// </summary>
    [Fact]
    public void DevinRewritesWithoutAVerifiedClientName()
    {
        Assert.True(HarnessRegistry.Devin.RewritesDispatch);
        Assert.Empty(HarnessRegistry.Devin.McpClientNames);
        Assert.Null(HarnessRegistry.Devin.McpClientPrefix);
    }

    /// <summary>Every MCP catalog row names a registry harness, and every registry catalog name has a row.</summary>
    [Fact]
    public void TheMcpCatalogAndTheRegistryAgreeOnHarnessNames()
    {
        HashSet<string> registryNames = HarnessRegistry.Rows
            .Where(row => row.McpCatalogName is not null)
            .Select(row => row.McpCatalogName!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (HarnessMcpEntry row in HarnessMcpCatalog.Rows)
        {
            // The Copilot coding agent is repository settings, not a hook harness: it has no token or adapter.
            if (row.Harness == "copilot-coding-agent")
            {
                continue;
            }

            Assert.Contains(row.Harness, registryNames);
        }

        foreach (string name in registryNames)
        {
            Assert.Contains(HarnessMcpCatalog.Rows, row => row.Harness == name);
        }
    }
}
