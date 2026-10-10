using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter Antigravity owned group: when enabled at project scope the
/// <see cref="AntigravityRenderer"/> returns an <c>antigravity</c>
/// <see cref="SquadRenderedBlock"/> whose splice sets the top-level <c>kyber-arbiter</c>
/// group of <c>.agents/hooks.json</c> to <c>PreToolUse</c> and <c>PostToolUse</c> matcher
/// groups matching <c>invoke_subagent</c> with one command hook; the render records the
/// <c>no-post-dispatch-feedback</c> degradation; and nothing renders when disabled or
/// under Global scope (Req 6.3, 8.1, 8.2, 22.2).
/// </summary>
public sealed class ArbiterAntigravityRenderingTests : IDisposable
{
    private const string BlockPath = ".agents/hooks.json";
    private const string GroupKey = "kyber-arbiter";
    private const string Matcher = "^invoke_subagent$";
    private const string Command = "kyber-weave-arbiter hook --harness antigravity";
    private const int HookTimeoutSeconds = 5;

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_ReturnsAntigravityBlock()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal("antigravity", block.Target);
        Assert.Equal(BlockPath, block.RelativePath);
        Assert.Equal(SquadHookBlockFormat.Antigravity, block.Format);

        Assert.Equal(
            ["PreToolUse", "PostToolUse"],
            block.Entries.Select(entry => entry.Container).ToArray());

        foreach (SquadRenderedBlockEntry entry in block.Entries)
        {
            JsonObject group = Assert.IsType<JsonObject>(entry.Entry);
            Assert.Equal(Matcher, group["matcher"]!.GetValue<string>());

            JsonArray hooks = Assert.IsType<JsonArray>(group["hooks"]);
            JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(hooks));
            Assert.Equal("command", hook["type"]!.GetValue<string>());
            Assert.Equal(Command, hook["command"]!.GetValue<string>());
            Assert.Equal(HookTimeoutSeconds, hook["timeout"]!.GetValue<int>());
        }
    }

    /// <summary>
    /// The block entries are the group's event arrays: run through the same splice the
    /// deployment plan uses, they must land under the single top-level
    /// <c>kyber-arbiter</c> group, because Antigravity reads any unknown top-level key as
    /// another group (design §10.4).
    /// </summary>
    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_SplicesIntoTheKyberArbiterGroup()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        (string Content, _) = SquadHookJsonBlock.SpliceContent(
            block.Format,
            existing: null,
            EntriesFor(block, "PreToolUse"),
            EntriesFor(block, "PostToolUse"));

        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(Content));
        Assert.Equal([GroupKey], root.Select(property => property.Key).ToArray());

        JsonObject group = Assert.IsType<JsonObject>(root[GroupKey]);
        Assert.Equal(["PreToolUse", "PostToolUse"], group.Select(property => property.Key).ToArray());
        foreach (string container in new[] { "PreToolUse", "PostToolUse" })
        {
            JsonArray matcherGroups = Assert.IsType<JsonArray>(group[container]);
            JsonObject matcherGroup = Assert.IsType<JsonObject>(Assert.Single(matcherGroups));
            Assert.Equal(Matcher, matcherGroup["matcher"]!.GetValue<string>());

            JsonArray hooks = Assert.IsType<JsonArray>(matcherGroup["hooks"]);
            JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(hooks));
            Assert.Equal("command", hook["type"]!.GetValue<string>());
            Assert.Equal(Command, hook["command"]!.GetValue<string>());
            Assert.Equal(HookTimeoutSeconds, hook["timeout"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EmitsNoWholeFile()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.NotNull(result.Blocks);
        Assert.NotEmpty(result.Blocks);
        Assert.DoesNotContain(result.Files, f => f.RelativePath == BlockPath);
    }

    /// <summary>
    /// Antigravity documents no post-dispatch result field ([F10]), so gating is
    /// pre-dispatch only: the render must say so through the §10.5 degradation rather
    /// than presenting the hook as full enforcement. The degradation is per target and
    /// agent, so every rendered agent carries one record naming it and its body digest.
    /// </summary>
    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_RecordsNoPostDispatchFeedbackDegradation()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        IReadOnlyList<SquadDegradationRecord> records = result.Degradations
            .Where(d => string.Equals(d.Code, "arbiter-not-enforced", StringComparison.Ordinal))
            .ToList();
        IReadOnlyList<string> agents = SquadSourceLoader.Load(_fixture.Path).Agents
            .Select(agent => agent.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            agents,
            records.Select(record => record.CanonicalIdentity).Order(StringComparer.Ordinal).ToList());
        Assert.All(
            records,
            record =>
            {
                Assert.Equal("antigravity", record.Target);
                Assert.Equal("no-post-dispatch-feedback", record.Details);
                Assert.Contains(record.CanonicalIdentity, agents);
            });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disabled")]
    [InlineData("global")]
    public async Task RenderAsync_WhenNotRendered_EmitsNoBlock(string mode)
    {
        SquadArbiterWiring? wiring = mode switch
        {
            "disabled" => new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds),
            "global" => new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            _ => null,
        };
        SquadDeploymentScope scope = string.Equals(mode, "global", StringComparison.Ordinal)
            ? SquadDeploymentScope.Global
            : SquadDeploymentScope.Project;

        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Antigravity],
            Scope: scope,
            Arbiter: wiring));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(result.Blocks is null || result.Blocks.Count == 0, "Expected no owned blocks when the Arbiter block is not rendered.");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == BlockPath);
        Assert.DoesNotContain(
            result.Degradations,
            d => string.Equals(d.Code, "arbiter-not-enforced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_EnabledArbiter_LeavesFilesByteForByte()
    {
        SquadRenderResult omitted = await RenderAsync(null, SquadDeploymentScope.Project);
        SquadRenderResult enabled = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.Equal(
            omitted.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList(),
            enabled.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList());
        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in omitted.Files
            .OrderBy(f => f.Target, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .Zip(enabled.Files
                .OrderBy(f => f.Target, StringComparer.Ordinal)
                .ThenBy(f => f.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Rendered bytes differ for {actual.Target}/{actual.RelativePath} when the Arbiter block is added.");
        }
    }

    private static List<JsonNode> EntriesFor(SquadRenderedBlock block, string container) =>
        block.Entries
            .Where(entry => string.Equals(entry.Container, container, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Entry)
            .ToList();

    private async Task<SquadRenderResult> RenderAsync(SquadArbiterWiring? arbiter, SquadDeploymentScope scope)
    {
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Antigravity],
            Scope: scope,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }
}
