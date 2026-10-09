using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the two Arbiter hook surfaces the <see cref="CopilotRenderer"/> lowers to: the
/// <c>hooks</c> frontmatter on VS Code agent files (<c>--harness copilot-vscode</c>, [F2]),
/// the owned Copilot CLI hook file at <c>.github/hooks/kyber-arbiter.json</c>
/// (<c>--harness copilot-cli</c>, [F3]), and that nothing hook-shaped renders when the
/// wiring is disabled or the scope is Global (Req 22.2).
/// </summary>
public sealed class ArbiterCopilotRenderingTests : IDisposable
{
    private static readonly string[] DispatcherAgents =
        [ArbiterSquadFixture.Conductor, ArbiterSquadFixture.Architect, ArbiterSquadFixture.ProductOwner, ArbiterSquadFixture.CodeReviewer];

    private static readonly string[] GuardedAgents =
        [ArbiterSquadFixture.CsharpDev, ArbiterSquadFixture.TestDev, ArbiterSquadFixture.GithubDevops];

    private static readonly string[] UnguardedAgents = [ArbiterSquadFixture.DocsDev, ArbiterSquadFixture.ResearchAgent];

    private const string CliHooksFilePath = ".github/hooks/kyber-arbiter.json";
    private const int HookTimeoutSeconds = 5;

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_WiresDispatcherAgentsWithPreAndPostToolUseFrontmatterHooks()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        foreach (string agentName in DispatcherAgents)
        {
            YamlMappingNode frontmatter = FrontmatterOf(AgentFile(result, agentName, SquadDeploymentScope.Project), agentName);
            AssertHookLists(frontmatter, agentName, expectPostToolUse: true);
        }
    }

    [Fact]
    public async Task RenderAsync_WiresGuardedAgentsWithASinglePreToolUseFrontmatterHookList()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        foreach (string agentName in GuardedAgents)
        {
            YamlMappingNode frontmatter = FrontmatterOf(AgentFile(result, agentName, SquadDeploymentScope.Project), agentName);
            AssertHookLists(frontmatter, agentName, expectPostToolUse: false);
        }
    }

    [Fact]
    public async Task RenderAsync_LeavesUnwiredAgentsWithoutHooksFrontmatter()
    {
        // Req 25.3: agents outside the dispatcher and guarded rosters — docs-dev and the
        // read-only research agent — must keep unguarded dispatch, so no hooks frontmatter.
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        foreach (string agentName in UnguardedAgents)
        {
            YamlMappingNode frontmatter = FrontmatterOf(AgentFile(result, agentName, SquadDeploymentScope.Project), agentName);
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("hooks")),
                $"Agent '{agentName}' must not carry a 'hooks' frontmatter entry.");
        }
    }

    [Fact]
    public async Task RenderAsync_WritesTheOwnedCopilotCliHookFileWithTaskMatcherAndBothShells()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadDeploymentFile hooksFile = Assert.Single(result.Files, file => file.RelativePath == CliHooksFilePath);
        Assert.Equal("copilot", hooksFile.Target);

        using JsonDocument document = JsonDocument.Parse(hooksFile.Content);
        JsonElement root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(1, root.GetProperty("version").GetInt32());

        JsonElement hooks = root.GetProperty("hooks");
        Assert.Equal(
            new HashSet<string> { "preToolUse", "postToolUse" },
            hooks.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal));

        foreach (string eventName in new[] { "preToolUse", "postToolUse" })
        {
            JsonElement entry = Assert.Single(hooks.GetProperty(eventName).EnumerateArray());
            Assert.Equal(
                new HashSet<string> { "type", "matcher", "bash", "powershell", "timeoutSec" },
                entry.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal));
            Assert.Equal("command", entry.GetProperty("type").GetString());
            Assert.Equal("task", entry.GetProperty("matcher").GetString());
            Assert.Equal("kyber-weave-arbiter hook --harness copilot-cli", entry.GetProperty("bash").GetString());
            Assert.Equal("kyber-weave-arbiter hook --harness copilot-cli", entry.GetProperty("powershell").GetString());
            Assert.Equal(HookTimeoutSeconds, entry.GetProperty("timeoutSec").GetInt32());
        }
    }

    [Fact]
    public async Task RenderAsync_DisabledArbiter_RendersNoHookSurfaces()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.DoesNotContain(result.Files, file => file.RelativePath == CliHooksFilePath);
        foreach (string agentName in DispatcherAgents.Concat(GuardedAgents))
        {
            YamlMappingNode frontmatter = FrontmatterOf(AgentFile(result, agentName, SquadDeploymentScope.Project), agentName);
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("hooks")),
                $"Agent '{agentName}' must not carry a 'hooks' frontmatter entry while the wiring is disabled.");
        }
    }

    [Fact]
    public async Task RenderAsync_EnabledArbiterUnderGlobalScope_RendersNoHookSurfaces()
    {
        // Req 22.4: a global install reads no project configuration, so even an enabled
        // wiring renders nothing hook-shaped. Under Global scope the .github/ prefix is
        // stripped, so agents resolve from agents/<name>.agent.md.
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Global);

        Assert.DoesNotContain(result.Files, file => file.RelativePath == CliHooksFilePath);
        foreach (string agentName in DispatcherAgents.Concat(GuardedAgents))
        {
            YamlMappingNode frontmatter = FrontmatterOf(AgentFile(result, agentName, SquadDeploymentScope.Global), agentName);
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("hooks")),
                $"Agent '{agentName}' must not carry a 'hooks' frontmatter entry under Global scope.");
        }
    }

    [Fact]
    public async Task RenderAsync_DisabledArbiter_RendersByteForByteAsOmittingTheWiring()
    {
        SquadRenderResult omitted = await RenderAsync(null, SquadDeploymentScope.Project);
        SquadRenderResult disabled = await RenderAsync(
            new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.Equal(
            omitted.Files.Select(file => (file.Target, file.RelativePath)).Order().ToList(),
            disabled.Files.Select(file => (file.Target, file.RelativePath)).Order().ToList());
        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in omitted.Files
            .OrderBy(file => file.Target, StringComparer.Ordinal)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .Zip(disabled.Files
                .OrderBy(file => file.Target, StringComparer.Ordinal)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Rendered bytes differ for {actual.Target}/{actual.RelativePath} when the " +
                "Arbiter wiring is disabled; a disabled wiring must not change today's render.");
        }
    }

    private async Task<SquadRenderResult> RenderAsync(SquadArbiterWiring? arbiter, SquadDeploymentScope scope)
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Copilot],
            Scope: scope,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    private static SquadDeploymentFile AgentFile(SquadRenderResult result, string agentName, SquadDeploymentScope scope)
    {
        string agentsPrefix = scope == SquadDeploymentScope.Project ? ".github/agents" : "agents";
        return Assert.Single(result.Files, file => file.RelativePath == $"{agentsPrefix}/{agentName}.agent.md");
    }

    private static void AssertHookLists(YamlMappingNode frontmatter, string agentName, bool expectPostToolUse)
    {
        YamlMappingNode hooks = RequireMapping(frontmatter, "hooks", agentName);
        string[] expectedEvents = expectPostToolUse ? ["PreToolUse", "PostToolUse"] : ["PreToolUse"];
        Assert.Equal(new HashSet<string>(expectedEvents, StringComparer.Ordinal), MappingKeys(hooks));

        foreach (string eventName in expectedEvents)
        {
            YamlSequenceNode entries = Assert.IsType<YamlSequenceNode>(
                hooks.Children[new YamlScalarNode(eventName)]);
            YamlMappingNode entry = Assert.IsType<YamlMappingNode>(Assert.Single(entries));

            // [F2]: a flat command list with no matcher, so every tool call reaches the hook.
            Assert.Equal(
                new HashSet<string> { "type", "command", "timeout" },
                MappingKeys(entry));
            Assert.Equal("command", RequireScalar(entry, "type", agentName));
            Assert.Equal(
                $"kyber-weave-arbiter hook --harness copilot-vscode --caller {agentName}",
                RequireScalar(entry, "command", agentName));
            Assert.Equal($"{HookTimeoutSeconds}", RequireScalar(entry, "timeout", agentName));
        }
    }

    private static YamlMappingNode FrontmatterOf(SquadDeploymentFile file, string agentName)
    {
        string text = Encoding.UTF8.GetString(file.Content.Span);
        const string delimiter = "---\n";
        Assert.True(
            text.StartsWith(delimiter, StringComparison.Ordinal),
            $"'{agentName}' is missing the opening frontmatter delimiter.");
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, $"'{agentName}' is missing a closing '---' frontmatter delimiter.");

        YamlStream stream = new();
        stream.Load(new StringReader(text[delimiter.Length..(end + 1)]));
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode node, string key, string identity)
    {
        Assert.True(
            node.Children.ContainsKey(new YamlScalarNode(key)),
            $"'{identity}' is missing a '{key}' frontmatter entry.");
        return Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);
    }

    private static string RequireScalar(YamlMappingNode node, string key, string identity)
    {
        Assert.True(
            node.Children.ContainsKey(new YamlScalarNode(key)),
            $"'{identity}' is missing a '{key}' value.");
        YamlScalarNode scalar = Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]);
        Assert.NotNull(scalar.Value);
        return scalar.Value;
    }

    private static HashSet<string> MappingKeys(YamlMappingNode node) =>
        node.Children.Keys
            .Select(key => Assert.IsType<YamlScalarNode>(key).Value)
            .ToHashSet(StringComparer.Ordinal)!;
}
