using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source through <see cref="CommandCodeRenderer"/> and pins
/// the Command Code agent and skill contract verified against <c>command-code</c> 1.84.0.
/// </summary>
public sealed class CommandCodeRendererContractTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>Command Code built-in tool names the lowering may emit.</summary>
    private static readonly string[] CommandCodeTools =
    [
        "read_file",
        "read_directory",
        "grep",
        "glob",
        "write_file",
        "edit_file",
        "shell_command",
        "shell_output",
        "web_search",
        "web_fetch"
    ];

    /// <summary>Names Command Code reserves for its bundled agents.</summary>
    private static readonly string[] ReservedAgentNames = ["explore", "plan", "review", "general"];

    private static Task<SquadRenderResult> RenderAsync(SquadDeploymentScope scope) =>
        new SquadRendererRegistry([new CommandCodeRenderer()]).RenderAsync(
            new SquadRenderRequest(ProductRoot, [SquadTarget.CommandCode], scope));

    private static YamlMappingNode Frontmatter(SquadDeploymentFile file)
    {
        string content = Encoding.UTF8.GetString(file.Content.Span);
        int end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
        YamlStream yaml = new();
        yaml.Load(new StringReader(content[4..end]));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    [Theory]
    [InlineData("commandcode")]
    [InlineData("command-code")]
    [InlineData("CommandCode")]
    public void ParseAcceptsCommandCodeTokens(string token)
    {
        Assert.Equal(SquadTarget.CommandCode, Assert.Single(SquadTargetCatalog.Parse([token])));
        Assert.Equal("commandcode", SquadTargetCatalog.GetToken(SquadTarget.CommandCode));
    }

    [Fact]
    public async Task EveryAgentRendersAtItsFlatAgentPathWithAnExplicitToolsList()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        foreach (SquadAgent agent in source.Agents)
        {
            Assert.DoesNotContain(agent.Name, ReservedAgentNames);
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".commandcode/agents/{agent.Name}.md");
            YamlMappingNode frontmatter = Frontmatter(file);

            Assert.Equal(agent.Name, frontmatter["name"].ToString());

            // Omitting tools grants nothing in Command Code; the renderer still always emits it.
            YamlSequenceNode tools = Assert.IsType<YamlSequenceNode>(frontmatter["tools"]);
            Assert.All(tools.Children, tool => Assert.Contains(tool.ToString(), CommandCodeTools));
            Assert.False(frontmatter.Children.ContainsKey(new YamlScalarNode("mcpServers")));
        }
    }

    [Fact]
    public async Task SkillsRenderUnderCommandCodeSkillsAndNeverUnderAgentsCompatTree()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> suppressed = SquadSharedIdentityProjection.SuppressedSkillIdentities(
            source,
            source.FallbackProfiles.Profiles.Values.SelectMany(p => p.SharedIdentities).ToHashSet(StringComparer.Ordinal));
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project);

        foreach (SquadSkill skill in source.Skills.Where(s => !suppressed.Contains(s.Name)))
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".commandcode/skills/{skill.Name}/SKILL.md");
        }

        Assert.All(result.Files, f => Assert.StartsWith(".commandcode/", f.RelativePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrimaryAgentAlsoRendersAnEntryPointSkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project);

        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Primary))
        {
            if (source.FallbackProfiles.Profiles[agent.Fallback].NoPrimaryAgent != "skill")
            {
                continue;
            }

            Assert.Contains(result.Files, f => f.RelativePath == $".commandcode/skills/{agent.Name}/SKILL.md");
            Assert.Contains(
                result.Degradations,
                d => d.CanonicalIdentity == agent.Name && d.Code == "role-skill-fallback");
        }
    }

    [Fact]
    public async Task GlobalScopeStripsTheCommandCodePrefix()
    {
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Global);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.All(result.Files, f => Assert.False(
            f.RelativePath.StartsWith(".commandcode/", StringComparison.Ordinal),
            f.RelativePath));
        Assert.Contains(result.Files, f => f.RelativePath.StartsWith("agents/", StringComparison.Ordinal));
        Assert.Contains(result.Files, f => f.RelativePath.StartsWith("skills/", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalRootIsDotCommandCodeUnderHome()
    {
        string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kw-commandcode-home"));
        SquadGlobalRoots roots = new(_ => null, home);

        Assert.Equal(Path.Combine(home, ".commandcode"), roots.ResolveGlobalRoot(SquadTarget.CommandCode));
    }

    [Theory]
    [InlineData("moonshotai/Kimi-K2.5[reasoningEffort=xhigh]", "moonshotai/Kimi-K2.5", "xhigh")]
    [InlineData("moonshotai/Kimi-K2.5", "moonshotai/Kimi-K2.5", null)]
    public void ReasoningEffortSuffixSplitsOntoCommandCodeLevels(string value, string model, string? effort)
    {
        Assert.Equal((model, effort), CommandCodeRenderer.ParseReasoningEffortSuffix(value));
    }

    [Fact]
    public void ReasoningEffortOutsideCommandCodeLevelsFailsClosed()
    {
        Assert.Throws<SquadRenderValidationException>(
            () => CommandCodeRenderer.ParseReasoningEffortSuffix("m[reasoningEffort=extreme]"));
    }
}
