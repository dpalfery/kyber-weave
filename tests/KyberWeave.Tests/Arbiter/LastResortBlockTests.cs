using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The last-resort block must be a block in the harness's own dialect: the host only
/// reaches it when the adapter's renderer itself throws, and a document the harness
/// does not read would let the dispatch through.
/// </summary>
public sealed class LastResortBlockTests
{
    [Theory]
    [InlineData("cursor", "permission", "deny")]
    [InlineData("copilot-cli", "permissionDecision", "deny")]
    public void For_KnownHarness_UsesItsOwnDenyDialect(string harness, string key, string expected)
    {
        using JsonDocument doc = JsonDocument.Parse(LastResortBlock.For(harness));

        Assert.Equal(expected, doc.RootElement.GetProperty(key).GetString());
        Assert.Contains(HookCommand.FailClosedCode, doc.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void For_EveryRegisteredPluginHarness_UsesThePluginDecisionDialect()
    {
        // A new plugin shim only blocks on `decision: "block"`; a Claude-shaped fallback
        // would let a double fault through. The token list is the single source.
        Assert.NotEmpty(PluginHookAdapters.Tokens);
        foreach (string token in PluginHookAdapters.Tokens)
        {
            using JsonDocument doc = JsonDocument.Parse(LastResortBlock.For(token));
            Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        }
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("copilot-vscode")]
    [InlineData("something-new")]
    [InlineData(null)]
    public void For_ClaudeShapedOrUnknownHarness_UsesHookSpecificOutput(string? harness)
    {
        using JsonDocument doc = JsonDocument.Parse(LastResortBlock.For(harness));

        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Contains(HookCommand.FailClosedCode, output.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cursor", "permission", "deny")]
    [InlineData("pi", "decision", "block")]
    public void Run_WhenAdapterCannotRenderItsOwnBlock_WritesTheHarnessDialect(
        string harness, string key, string expected)
    {
        HarnessAdapterRegistry registry = new([new ThrowingAdapter(harness)]);
        HookCommand command = new(registry, _ => throw new InvalidOperationException("no config"));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = command.Run(harness, null, """{"tool_name":"Task"}""", stdout, stderr);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(expected, doc.RootElement.GetProperty(key).GetString());
    }

    private sealed class ThrowingAdapter(string token) : IHarnessHookAdapter
    {
        public string HarnessToken => token;

        public bool IsPassThrough(JsonElement payload, string? renderedCaller) => false;

        public string Handle(
            JsonElement payload, string rawJson, string? renderedCaller, KyberWeaveConfig config, HookContext context) =>
            throw new InvalidOperationException("adapter fault");

        public string RenderFailClosed(string detail, string? renderedCaller, string rawStdin, string decisionId) =>
            throw new InvalidOperationException("renderer fault");
    }
}
