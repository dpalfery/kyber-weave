using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter Claude hook rendering contract: dispatcher agents and the
/// <c>/conductor</c> entry-point skill carry Pre/Post dispatch hooks with the anchored
/// <c>^(Agent|Task)$</c> matcher, guarded implementation specialists carry the Read guard
/// hook with <c>^(Read|Grep|Glob|Bash)$</c>, and nothing is rendered when the wiring is
/// null, disabled, or the scope is Global.
/// </summary>
public sealed class ArbiterClaudeRenderingTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly string[] DispatcherAgents =
    [
        ArbiterSquadFixture.Conductor,
        ArbiterSquadFixture.Architect,
        ArbiterSquadFixture.ProductOwner,
        ArbiterSquadFixture.CodeReviewer
    ];

    private static readonly string[] GuardedAgents =
    [
        ArbiterSquadFixture.CsharpDev,
        ArbiterSquadFixture.TestDev,
        ArbiterSquadFixture.GithubDevops
    ];

    private const string DispatchMatcher = "^(Agent|Task)$";
    private const string ReadGuardMatcher = "^(Read|Grep|Glob|Bash)$";
    private const string HandbackMatcher = "^SubagentHandback$";

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_Dispatchers_CarryPreAndPostDispatchHooks()
    {
        const int timeoutSeconds = 5;
        SquadRenderResult result = await RenderClaudeAsync(
            _fixture.Path,
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string agent in DispatcherAgents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/agents/{agent}.md");
            YamlMappingNode frontmatter = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent);

            AssertDispatchHooks(frontmatter, agent, timeoutSeconds);
        }
    }

    [Fact]
    public async Task RenderAsync_GuardedAgents_CarryTheReadGuardHook()
    {
        const int timeoutSeconds = 5;
        SquadRenderResult result = await RenderClaudeAsync(
            _fixture.Path,
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string agent in GuardedAgents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/agents/{agent}.md");
            YamlMappingNode frontmatter = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent);

            AssertReadGuardHook(frontmatter, agent, timeoutSeconds);
        }
    }

    [Fact]
    public async Task RenderAsync_UnguardedDispatchTargets_CarryOnlyTheHandbackHook()
    {
        // docs-dev and research-agent are neither dispatchers nor guarded, but both are named
        // in some agent's delegates-to roster, so under the 6.6 contract their only hook is
        // the hand-back PreToolUse entry: no dispatch, guard, or PostToolUse entry.
        const int timeoutSeconds = 5;
        SquadRenderResult result = await RenderClaudeAsync(
            _fixture.Path,
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string agent in new[] { ArbiterSquadFixture.DocsDev, ArbiterSquadFixture.ResearchAgent })
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".claude/agents/{agent}.md");
            YamlMappingNode frontmatter = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent);

            YamlMappingNode hooks = RequireMapping(frontmatter, "hooks", agent);
            Assert.Single(hooks.Children);

            YamlSequenceNode entries = RequireSequence(hooks, "PreToolUse", agent);
            YamlMappingNode entry = Assert.Single(entries.Children.OfType<YamlMappingNode>());
            Assert.Equal(HandbackMatcher, RequireScalar(entry, "matcher", agent));

            YamlSequenceNode inner = RequireSequence(entry, "hooks", agent);
            YamlMappingNode hook = Assert.Single(inner.Children.OfType<YamlMappingNode>());
            Assert.Equal("command", RequireScalar(hook, "type", agent));
            Assert.Equal(
                $"kyber-weave-arbiter hook --harness claude --caller {agent}",
                RequireScalar(hook, "command", agent));
            Assert.Equal(timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), RequireScalar(hook, "timeout", agent));
        }
    }

    [Fact]
    public async Task RenderAsync_ConductorEntryPointSkill_CarriesDispatchHooks()
    {
        const int timeoutSeconds = 5;
        SquadRenderResult result = await RenderClaudeAsync(
            ProductRoot,
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".claude/skills/conductor/SKILL.md");
        YamlMappingNode frontmatter = SplitFrontmatter(
            Encoding.UTF8.GetString(skillFile.Content.Span),
            "conductor-skill");

        AssertDispatchHooks(frontmatter, ArbiterSquadFixture.Conductor, timeoutSeconds);
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

    private static void AssertDispatchHooks(YamlMappingNode frontmatter, string agent, int timeoutSeconds)
    {
        YamlMappingNode hooks = RequireMapping(frontmatter, "hooks", agent);

        foreach (string phase in new[] { "PreToolUse", "PostToolUse" })
        {
            YamlSequenceNode entries = RequireSequence(hooks, phase, agent);
            YamlMappingNode entry = Assert.Single(
                entries.Children.OfType<YamlMappingNode>(),
                e => string.Equals(RequireScalar(e, "matcher", agent), DispatchMatcher, StringComparison.Ordinal));
            Assert.Equal(DispatchMatcher, RequireScalar(entry, "matcher", agent));

            if (phase == "PreToolUse")
            {
                AssertOnlyHandbackBeside(entries, DispatchMatcher, agent);
            }

            YamlSequenceNode inner = RequireSequence(entry, "hooks", agent);
            YamlMappingNode hook = Assert.Single(inner.Children.OfType<YamlMappingNode>());
            Assert.Equal("command", RequireScalar(hook, "type", agent));
            Assert.Equal(
                $"kyber-weave-arbiter hook --harness claude --caller {agent}",
                RequireScalar(hook, "command", agent));
            Assert.Equal(timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), RequireScalar(hook, "timeout", agent));
        }
    }

    private static void AssertReadGuardHook(YamlMappingNode frontmatter, string agent, int timeoutSeconds)
    {
        YamlMappingNode hooks = RequireMapping(frontmatter, "hooks", agent);

        Assert.False(
            hooks.Children.ContainsKey(new YamlScalarNode("PostToolUse")),
            $"Guarded agent '{agent}' must not carry a PostToolUse hook.");

        YamlSequenceNode entries = RequireSequence(hooks, "PreToolUse", agent);
        YamlMappingNode entry = Assert.Single(
            entries.Children.OfType<YamlMappingNode>(),
            e => string.Equals(RequireScalar(e, "matcher", agent), ReadGuardMatcher, StringComparison.Ordinal));
        Assert.Equal(ReadGuardMatcher, RequireScalar(entry, "matcher", agent));
        AssertOnlyHandbackBeside(entries, ReadGuardMatcher, agent);

        YamlSequenceNode inner = RequireSequence(entry, "hooks", agent);
        YamlMappingNode hook = Assert.Single(inner.Children.OfType<YamlMappingNode>());
        Assert.Equal("command", RequireScalar(hook, "type", agent));
        Assert.Equal(
            $"kyber-weave-arbiter hook --harness claude --caller {agent}",
            RequireScalar(hook, "command", agent));
        Assert.Equal(timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), RequireScalar(hook, "timeout", agent));
    }

    /// <summary>
    /// Under the 6.6 hand-back contract a dispatch target's PreToolUse list holds its primary
    /// entry plus at most one <c>^SubagentHandback$</c> entry; a dispatcher that is also
    /// guarded additionally keeps the read-guard entry, so that matcher may appear too.
    /// No other matcher may appear.
    /// </summary>
    private static void AssertOnlyHandbackBeside(YamlSequenceNode entries, string primaryMatcher, string agent)
    {
        string otherPrimary = string.Equals(primaryMatcher, DispatchMatcher, StringComparison.Ordinal)
            ? ReadGuardMatcher
            : DispatchMatcher;
        YamlMappingNode[] others = entries.Children.OfType<YamlMappingNode>()
            .Where(e => !string.Equals(RequireScalar(e, "matcher", agent), primaryMatcher, StringComparison.Ordinal))
            .ToArray();

        YamlMappingNode[] handback = others
            .Where(e => string.Equals(RequireScalar(e, "matcher", agent), HandbackMatcher, StringComparison.Ordinal))
            .ToArray();
        YamlMappingNode[] overlap = others
            .Where(e => string.Equals(RequireScalar(e, "matcher", agent), otherPrimary, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            handback.Length <= 1 && overlap.Length <= 1 && others.Length == handback.Length + overlap.Length,
            $"'{agent}' carries an unexpected PreToolUse entry beside its primary entry; only the hand-back entry and the dispatcher-guarded overlap entry are allowed.");
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
