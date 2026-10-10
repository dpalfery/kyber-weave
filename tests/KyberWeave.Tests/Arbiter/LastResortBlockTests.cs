using System.Text;
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
    [Fact]
    public void For_EveryRegisteredToken_InAnyCase_StillGetsItsOwnDialect()
    {
        // The registry resolves --harness case-insensitively, so `--harness OpenCode`
        // finds the OpenCode adapter while this method was still matching Ordinal. A
        // double fault then emitted a Claude-shaped document to a plugin shim, which
        // reads it as no-block: a fail-open double fault. Every token must land on its
        // own dialect whatever case it arrives in.
        HarnessAdapterRegistry registry =
            HarnessAdapterRegistry.CreateDefault(new NeverConsultedEngine());
        List<string> tokens = [.. registry.Tokens.OrderBy(token => token, StringComparer.Ordinal)];

        Assert.NotEmpty(tokens);
        foreach (string token in tokens)
        {
            string expected = DialectOf(token);
            foreach (string variant in Variants(token))
            {
                Assert.True(
                    string.Equals(expected, DialectOf(variant), StringComparison.Ordinal),
                    $"'{variant}' should keep the '{token}' dialect '{expected}'.");
            }
        }
    }

    [Fact]
    public void For_APluginTokenInTheWrongCase_StillGetsThePluginDecisionDialect()
    {
        // The concrete reported double fault: a plugin shim only blocks on
        // `decision: "block"`, and the Claude shape would let a double fault through.
        foreach (string token in PluginHookAdapters.Tokens)
        {
            foreach (string variant in Variants(token))
            {
                using JsonDocument doc = JsonDocument.Parse(LastResortBlock.For(variant));

                Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
                Assert.False(doc.RootElement.TryGetProperty("hookSpecificOutput", out _));
            }
        }
    }

    private static IEnumerable<string> Variants(string token) =>
    [
        token,
        token.ToUpperInvariant(),
        MixCase(token),
        $"  {token}  ",
    ];

    /// <summary>
    /// The registered tokens are all lower case, so <c>token</c> is already the lower
    /// case variant; <see cref="MixCase"/> supplies the mixed one.
    /// </summary>
    private static string MixCase(string token)
    {
        StringBuilder mixed = new(token.Length);
        for (int i = 0; i < token.Length; i++)
        {
            char c = token[i];
            mixed.Append(i % 2 == 0 ? char.ToUpperInvariant(c) : c);
        }

        return mixed.ToString();
    }

    /// <summary>
    /// The dialect's identity: its sorted top-level property names. The reason text
    /// embeds the token as the caller wrote it, so only the shape can be compared.
    /// </summary>
    private static string DialectOf(string? harness)
    {
        using JsonDocument doc = JsonDocument.Parse(LastResortBlock.For(harness));
        return string.Join(
            ",",
            doc.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
    }

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

    /// <summary>Composed only so the registry can be built; no adapter is ever invoked.</summary>
    private sealed class NeverConsultedEngine : IHookDecisionEngine
    {
        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            throw new InvalidOperationException("this test composes the registry only");

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            throw new InvalidOperationException("this test composes the registry only");
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
