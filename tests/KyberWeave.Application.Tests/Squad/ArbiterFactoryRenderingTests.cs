using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Fixtures;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Factory worker-guard hooks beside the existing dispatch hooks: a <c>PreToolUse</c> group matching every
/// tool, a <c>SessionStart</c> and <c>SessionEnd</c> with no matcher, and the dispatch's own <c>PostToolUse</c> group,
/// each running <c>kyber-weave-arbiter worker-guard --harness factory</c>. The same entries are owned by signature, so
/// re-installing replaces them and uninstall removes them.
/// </summary>
public sealed class ArbiterFactoryRenderingTests : IDisposable
{
    private const string BlockPath = ".factory/hooks.json";
    private const string DispatchCommand = "kyber-weave-arbiter hook --harness factory";
    private const string WorkerGuardCommand = "kyber-weave-arbiter worker-guard --harness factory";
    private const int HookTimeoutSeconds = 30;

    private readonly InMemoryFileSystem _files = new();
    private readonly ArbiterSquadFixture _fixture;

    public ArbiterFactoryRenderingTests()
    {
        _fixture = ArbiterSquadFixture.CreateInMemory(_files);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_AddsTheWorkerGuardHooksBesideTheDispatchHooks()
    {
        SquadRenderedBlock block = await RenderedBlockAsync();

        Assert.Equal(
            ["PreToolUse", "PostToolUse", "SubagentStop", "PreToolUse", "PostToolUse", "SessionStart", "SessionEnd"],
            block.Entries.Select(entry => entry.Container).ToArray());

        AssertGroup(block.Entries[0], matcher: "Task", DispatchCommand);
        AssertGroup(block.Entries[1], matcher: "Task", DispatchCommand);
        AssertGroup(block.Entries[2], matcher: null, DispatchCommand);
        AssertGroup(block.Entries[3], matcher: "*", WorkerGuardCommand);
        AssertGroup(block.Entries[4], matcher: "Task", WorkerGuardCommand);
        AssertGroup(block.Entries[5], matcher: null, WorkerGuardCommand);
        AssertGroup(block.Entries[6], matcher: null, WorkerGuardCommand);
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_WorkerGuardEntriesAreRecognizedAsSquadOwned()
    {
        SquadRenderedBlock block = await RenderedBlockAsync();

        Assert.All(block.Entries, entry =>
            Assert.True(
                SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Factory, entry.Entry),
                $"Entry in container '{entry.Container}' is not recognized as Squad-owned."));
    }

    [Fact]
    public async Task SpliceThenRemove_OwnsAndRemovesEveryWorkerGuardEntry()
    {
        SquadRenderedBlock block = await RenderedBlockAsync();

        List<JsonNode> In(string container) =>
            [.. block.Entries.Where(entry => entry.Container == container).Select(entry => entry.Entry.DeepClone())];

        (string content, IReadOnlyList<SquadHookOwnedEntry> owned) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Factory,
            null,
            In("PreToolUse"),
            In("PostToolUse"),
            In("SubagentStop"),
            In("SessionStart"),
            In("SessionEnd"));

        Assert.Equal(4, Count(content, WorkerGuardCommand));
        Assert.Equal(3, Count(content, DispatchCommand));
        Assert.Equal(7, owned.Count);
        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(content));
        Assert.Equal(
            ["PreToolUse", "PostToolUse", "SubagentStop", "SessionStart", "SessionEnd"],
            root.Select(property => property.Key).ToArray());

        string removed = SquadHookJsonBlock.RemoveContent(SquadHookBlockFormat.Factory, content);

        Assert.DoesNotContain(WorkerGuardCommand, removed, StringComparison.Ordinal);
        Assert.DoesNotContain(DispatchCommand, removed, StringComparison.Ordinal);
        Assert.Equal("{}", removed.Trim());
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Claude)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void SpliceContent_NonFactoryFormat_RefusesASessionEventEntry(SquadHookBlockFormat format)
    {
        Assert.Throws<InvalidOperationException>(() => SquadHookJsonBlock.SpliceContent(
            format,
            null,
            [],
            [],
            sessionStart: [Group(matcher: null, WorkerGuardCommand)]));
    }

    private async Task<SquadRenderedBlock> RenderedBlockAsync()
    {
        SquadRendererRegistry registry = _fixture.Registry([new FactoryRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds)).Prepared(_fixture));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal(BlockPath, block.RelativePath);
        return block;
    }

    private static void AssertGroup(SquadRenderedBlockEntry entry, string? matcher, string command)
    {
        JsonObject group = Assert.IsType<JsonObject>(entry.Entry);
        if (matcher is null)
        {
            Assert.False(group.ContainsKey("matcher"));
        }
        else
        {
            Assert.Equal(matcher, group["matcher"]!.GetValue<string>());
        }

        JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(group["hooks"])));
        Assert.Equal("command", hook["type"]!.GetValue<string>());
        Assert.Equal(command, hook["command"]!.GetValue<string>());
        Assert.Equal(HookTimeoutSeconds, hook["timeout"]!.GetValue<int>());
    }

    private static JsonObject Group(string? matcher, string command)
    {
        JsonObject group = new();
        if (matcher is not null)
        {
            group["matcher"] = matcher;
        }

        group["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = HookTimeoutSeconds,
        });
        return group;
    }

    private static int Count(string text, string needle) => text.Split(needle, StringSplitOptions.None).Length - 1;
}
