using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter Claude hand-back hook contract (Req 21.3, Q15 option (a)): every agent
/// named in any dispatcher's <c>delegates-to</c> roster carries a <c>PreToolUse</c> entry
/// matched on <c>^SubagentHandback$</c> with <c>--caller &lt;agent&gt;</c>, alongside any
/// dispatch or guard entry it already has. Nothing is rendered when the wiring is null or
/// disabled, or the scope is Global.
/// </summary>
public sealed class ArbiterClaudeHandbackRenderingTests : IDisposable
{
    private const string HandbackMatcher = "^SubagentHandback$";
    private const string DispatchMatcher = "^(Agent|Task)$";
    private const string ReadGuardMatcher = "^(Read|Grep|Glob|Bash)$";

    private static readonly string[] DispatchTargets =
    [
        ArbiterSquadFixture.Architect,
        ArbiterSquadFixture.CsharpDev,
        ArbiterSquadFixture.DocsDev,
        ArbiterSquadFixture.TestDev,
        ArbiterSquadFixture.ResearchAgent
    ];

    private static readonly string[] NonTargets =
    [
        ArbiterSquadFixture.Conductor,
        ArbiterSquadFixture.ProductOwner,
        ArbiterSquadFixture.CodeReviewer,
        ArbiterSquadFixture.GithubDevops
    ];

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_DispatchTargets_CarryTheHandbackHook()
    {
        const int timeoutSeconds = 5;
        SquadRenderResult result = await RenderClaudeAsync(
            _fixture.Path,
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string agent in DispatchTargets)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/agents/{agent}.md");
            YamlMappingNode frontmatter = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent);

            AssertHandbackHook(frontmatter, agent, timeoutSeconds);
        }
    }

    [Fact]
    public async Task RenderAsync_HandbackHook_CoexistsWithDispatchAndGuardEntries()
    {
        const int timeoutSeconds = 5;
        SquadRenderResult result = await RenderClaudeAsync(
            _fixture.Path,
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // architect is a dispatcher and a dispatch target: PreToolUse keeps the dispatch
        // entry and gains the hand-back entry; PostToolUse keeps the dispatch entry.
        YamlMappingNode architectFrontmatter = SplitFrontmatter(
            Encoding.UTF8.GetString(Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/agents/{ArbiterSquadFixture.Architect}.md").Content.Span),
            ArbiterSquadFixture.Architect);
        YamlMappingNode architectHooks = RequireMapping(architectFrontmatter, "hooks", ArbiterSquadFixture.Architect);
        YamlSequenceNode architectPre = RequireSequence(architectHooks, "PreToolUse", ArbiterSquadFixture.Architect);
        Assert.Equal(2, architectPre.Children.Count);
        Assert.Contains(architectPre.Children.OfType<YamlMappingNode>(), e => MatcherOf(e) == DispatchMatcher);
        Assert.Contains(architectPre.Children.OfType<YamlMappingNode>(), e => MatcherOf(e) == HandbackMatcher);
        YamlSequenceNode architectPost = RequireSequence(architectHooks, "PostToolUse", ArbiterSquadFixture.Architect);
        YamlMappingNode architectPostEntry = Assert.Single(architectPost.Children.OfType<YamlMappingNode>());
        Assert.Equal(DispatchMatcher, MatcherOf(architectPostEntry));

        // csharp-dev is guarded and a dispatch target: PreToolUse keeps the guard entry
        // and gains the hand-back entry; no PostToolUse.
        YamlMappingNode workerFrontmatter = SplitFrontmatter(
            Encoding.UTF8.GetString(Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/agents/{ArbiterSquadFixture.CsharpDev}.md").Content.Span),
            ArbiterSquadFixture.CsharpDev);
        YamlMappingNode workerHooks = RequireMapping(workerFrontmatter, "hooks", ArbiterSquadFixture.CsharpDev);
        YamlSequenceNode workerPre = RequireSequence(workerHooks, "PreToolUse", ArbiterSquadFixture.CsharpDev);
        Assert.Equal(2, workerPre.Children.Count);
        Assert.Contains(workerPre.Children.OfType<YamlMappingNode>(), e => MatcherOf(e) == ReadGuardMatcher);
        Assert.Contains(workerPre.Children.OfType<YamlMappingNode>(), e => MatcherOf(e) == HandbackMatcher);
        Assert.False(
            workerHooks.Children.ContainsKey(new YamlScalarNode("PostToolUse")),
            $"Guarded agent '{ArbiterSquadFixture.CsharpDev}' must not carry a PostToolUse hook.");
    }

    [Fact]
    public async Task RenderAsync_NonTargets_CarryNoHandbackHook()
    {
        SquadRenderResult result = await RenderClaudeAsync(
            _fixture.Path,
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string agent in NonTargets)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/agents/{agent}.md");
            YamlMappingNode frontmatter = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent);

            if (!frontmatter.Children.TryGetValue(new YamlScalarNode("hooks"), out YamlNode? hooksNode))
            {
                continue;
            }

            YamlMappingNode hooks = Assert.IsType<YamlMappingNode>(hooksNode);
            if (hooks.Children.TryGetValue(new YamlScalarNode("PreToolUse"), out YamlNode? preNode))
            {
                YamlSequenceNode pre = Assert.IsType<YamlSequenceNode>(preNode);
                Assert.DoesNotContain(
                    pre.Children.OfType<YamlMappingNode>(),
                    e => string.Equals(MatcherOf(e), HandbackMatcher, StringComparison.Ordinal));
            }
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disabled")]
    [InlineData("global")]
    public async Task RenderAsync_WhenNotRendered_EmitsNoHooks(string mode)
    {
        SquadArbiterWiring? wiring = mode switch
        {
            "disabled" => new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: 5),
            "global" => new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5),
            _ => null
        };
        SquadDeploymentScope scope = string.Equals(mode, "global", StringComparison.Ordinal)
            ? SquadDeploymentScope.Global
            : SquadDeploymentScope.Project;

        SquadRenderResult result = await RenderClaudeAsync(_fixture.Path, scope, wiring);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotEmpty(result.Files);

        foreach (SquadDeploymentFile file in result.Files.Where(f => f.RelativePath.EndsWith(".md", StringComparison.Ordinal)))
        {
            YamlMappingNode frontmatter = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                file.RelativePath);

            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("hooks")),
                $"File '{file.RelativePath}' must not carry hooks when Arbiter is {mode}.");
        }
    }

    private static async Task<SquadRenderResult> RenderClaudeAsync(
        string sourceDirectory,
        SquadDeploymentScope scope,
        SquadArbiterWiring? arbiter)
    {
        ClaudeRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: sourceDirectory,
            Targets: [SquadTarget.Claude],
            Scope: scope,
            Arbiter: arbiter);

        return await renderer.RenderAsync(request);
    }

    private static void AssertHandbackHook(YamlMappingNode frontmatter, string agent, int timeoutSeconds)
    {
        YamlMappingNode hooks = RequireMapping(frontmatter, "hooks", agent);
        YamlSequenceNode entries = RequireSequence(hooks, "PreToolUse", agent);

        YamlMappingNode entry = Assert.Single(
            entries.Children.OfType<YamlMappingNode>(),
            e => string.Equals(MatcherOf(e), HandbackMatcher, StringComparison.Ordinal));
        Assert.Equal(HandbackMatcher, RequireScalar(entry, "matcher", agent));

        YamlSequenceNode inner = RequireSequence(entry, "hooks", agent);
        YamlMappingNode hook = Assert.Single(inner.Children.OfType<YamlMappingNode>());
        Assert.Equal("command", RequireScalar(hook, "type", agent));
        Assert.Equal(
            $"kyber-weave-arbiter hook --harness claude --caller {agent}",
            RequireScalar(hook, "command", agent));
        Assert.Equal(timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), RequireScalar(hook, "timeout", agent));
    }

    private static string MatcherOf(YamlMappingNode entry)
    {
        if (!entry.Children.TryGetValue(new YamlScalarNode("matcher"), out YamlNode? value))
        {
            return string.Empty;
        }

        return Assert.IsType<YamlScalarNode>(value).Value ?? string.Empty;
    }

    private static YamlMappingNode SplitFrontmatter(string text, string identity)
    {
        const string delimiter = "---\n";
        Assert.True(
            text.StartsWith(delimiter, StringComparison.Ordinal),
            $"'{identity}' is missing the opening frontmatter delimiter.");
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, $"'{identity}' is missing a closing '---' frontmatter delimiter.");

        string yaml = text[delimiter.Length..(end + 1)];

        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        return Assert.IsType<YamlMappingNode>(value);
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        return Assert.IsType<YamlSequenceNode>(value);
    }

    private static string RequireScalar(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        return Assert.IsType<YamlScalarNode>(value).Value
            ?? throw new InvalidOperationException($"'{identity}' key '{key}' has a null scalar value.");
    }
}
