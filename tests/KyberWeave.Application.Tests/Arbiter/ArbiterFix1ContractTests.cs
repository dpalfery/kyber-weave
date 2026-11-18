using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;
using YamlDotNet.RepresentationModel;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task fix1 (Phase 1 review fixes, council review 10.1), F4: an agent that is both a dispatcher and
/// guarded keeps both hooks. F1 lives in <c>ArbiterFailClosedHostTests</c>, F2 in <c>MacKeychainWriteTests</c> and
/// F3 in <c>HandWrittenUnsafeTests</c>.
/// </summary>
public sealed class ArbiterFix1ContractTests : IDisposable
{
    private readonly InMemoryFileSystem _files = new();
    private readonly ArbiterSquadFixture _fixture;

    public ArbiterFix1ContractTests() => _fixture = ArbiterSquadFixture.CreateInMemory(_files);

    public void Dispose() => _fixture.Dispose();

    // F4: an agent that is both dispatcher and guarded keeps BOTH hooks.
    [Fact]
    public async Task Fix4_DispatcherPlusGuardedAgent_KeepsBothHooks()
    {
        // Make dev-medium (worker profile, dispatch target) a dispatcher too.
        string agentPath = Path.Combine(_fixture.Path, "agents", "dev-medium.md");
        _files.WriteAllText(agentPath, """
            ---
            schema: kyber-squad.agent/v1
            name: dev-medium
            description: Arbiter fixture agent exercising the worker profile.
            invocation: subagent
            model-profile: general
            capability-profile: worker
            copilot-tools: [vscode, read]
            delegates-to: [research-agent]
            fallback: role-skill
            aliases: []
            ---
            Follow the worker instruction body.
            """);

        ClaudeRenderer renderer = new();
        SquadRenderResult result = await renderer.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)).Prepared(_fixture));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile file = Assert.Single(
            result.Files, f => f.RelativePath == ".claude/agents/dev-medium.md");
        YamlMappingNode frontmatter = SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span));
        YamlMappingNode hooks = RequireMapping(frontmatter, "hooks");
        YamlSequenceNode pre = RequireSequence(hooks, "PreToolUse");

        string[] matchers = pre.Children.OfType<YamlMappingNode>()
            .Select(MatcherOf).ToArray();
        Assert.Contains("^(Agent|Task)$", matchers);
        Assert.Contains("^(Read|Grep|Glob|Bash|PowerShell)$", matchers);
        Assert.Contains("^SubagentHandback$", matchers);
    }

    private static YamlMappingNode SplitFrontmatter(string text)
    {
        const string delimiter = "---\n";
        Assert.True(text.StartsWith(delimiter, StringComparison.Ordinal), "Missing frontmatter delimiter.");
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, "Missing closing frontmatter delimiter.");
        string yaml = text[delimiter.Length..(end + 1)];
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            throw new InvalidOperationException($"Frontmatter is missing required key '{key}'.");
        }

        return Assert.IsType<YamlMappingNode>(value);
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            throw new InvalidOperationException($"Frontmatter is missing required key '{key}'.");
        }

        return Assert.IsType<YamlSequenceNode>(value);
    }

    private static string MatcherOf(YamlMappingNode entry)
    {
        if (!entry.Children.TryGetValue(new YamlScalarNode("matcher"), out YamlNode? value))
        {
            return string.Empty;
        }

        return Assert.IsType<YamlScalarNode>(value).Value ?? string.Empty;
    }
}
