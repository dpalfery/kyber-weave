using System.Text;
using KyberWeave.Tests.Fakes;
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

    /// <summary>
    /// The capability→tool lowering this suite pins. Declared independently of the renderer so a
    /// change to either side has to be made deliberately in both. <c>network.publish</c> and
    /// <c>delegate</c> are absent because Command Code has no built-in tool for either on a
    /// subagent.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("filesystem.read", ["read_file"]),
        ("filesystem.search", ["read_directory", "grep", "glob"]),
        ("filesystem.write", ["write_file", "edit_file"]),
        ("process.execute", ["shell_command", "shell_output"]),
        ("network.read", ["web_search", "web_fetch"])
    ];

    /// <summary>Fixed built-in emission order, so a rendered agent file is byte-stable.</summary>
    private static readonly string[] BuiltInToolOrder =
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

    private static Task<SquadRenderResult> RenderAsync(SquadDeploymentScope scope) =>
        new SquadRendererRegistry([new CommandCodeRenderer()]).RenderAsync(
            new SquadRenderRequest(ProductRoot, [SquadTarget.CommandCode], scope));

    /// <summary>
    /// The <c>tools</c> value an agent must render: the allow-only built-in lowering followed by
    /// the MCP tool names the agent is entitled to, in server-then-tool order.
    /// </summary>
    /// <remarks>
    /// The MCP tail is the point of naming tools at all on Command Code — a subagent reaches an
    /// MCP tool only when its own <c>tools</c> list names it — so it comes from
    /// <see cref="SquadMcpGrantProjection"/> rather than from the renderer.
    /// </remarks>
    private static IReadOnlyList<string> ExpectedTools(SquadSource source, SquadAgent agent)
    {
        SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
        HashSet<string> granted = new(StringComparer.Ordinal);
        foreach ((string capability, string[] tools) in CapabilityToolContract)
        {
            if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision == SquadPermissionDecision.Allow)
            {
                granted.UnionWith(tools);
            }
        }

        return
        [
            .. BuiltInToolOrder.Where(granted.Contains),
            .. SquadMcpGrantProjection.EntitledKyberWeaveToolNames(source, agent, SquadTarget.CommandCode)
        ];
    }

    private static IReadOnlyList<string> RenderedTools(
        SquadRenderResult result,
        string agentName)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".commandcode/agents/{agentName}.md");
        YamlSequenceNode tools = Assert.IsType<YamlSequenceNode>(Frontmatter(file)["tools"]);
        return [.. tools.Children.Select(tool => tool.ToString())];
    }

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
        SquadSource source = TestSquadCorpus.CanonicalSource;
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
            Assert.All(
                tools.Children.Select(tool => tool.ToString()),
                tool => Assert.True(
                    CommandCodeTools.Contains(tool, StringComparer.Ordinal) ||
                    tool.StartsWith("mcp__", StringComparison.Ordinal),
                    $"Agent '{agent.Name}' renders tool '{tool}', which is neither a documented "
                    + "Command Code built-in nor a qualified MCP tool name."));
            Assert.False(frontmatter.Children.ContainsKey(new YamlScalarNode("mcpServers")));
        }
    }

    /// <summary>
    /// Every agent is granted the <c>kyber-weave</c> roster by qualified name, so a subagent
    /// reaches the coding-standards tool it must follow.
    /// </summary>
    /// <remarks>
    /// Command Code gives a subagent no MCP tool it does not name in its own <c>tools</c> list, so
    /// an unnamed server is a server the agent cannot call. The orchestrator is included: the
    /// entitlement is the file-read grant alone, not the profile's identity.
    /// </remarks>
    [Fact]
    public async Task EveryAgentNamesTheKyberWeaveRosterToolsItIsEntitledTo()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        IReadOnlyList<string> kyberWeaveTools = SquadMcpGrantProjection.KyberWeaveToolNames(source);
        Assert.NotEmpty(kyberWeaveTools);

        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        List<string> grantedAgents = [];
        foreach (SquadAgent agent in source.Agents)
        {
            IReadOnlyList<string> tools = RenderedTools(result, agent.Name);
            IReadOnlyList<string> expected = ExpectedTools(source, agent);
            Assert.Equal(expected, tools);

            if (SquadMcpGrantProjection.GrantsKyberWeave(source, agent, SquadTarget.CommandCode))
            {
                grantedAgents.Add(agent.Name);
                Assert.All(
                    kyberWeaveTools,
                    tool => Assert.Contains(tool, tools, StringComparer.Ordinal));

                // The roster keeps server-then-tool order wherever it lands in the list.
                Assert.Equal(
                    kyberWeaveTools,
                    tools.Where(kyberWeaveTools.Contains).ToArray());
            }
        }

        Assert.Contains("conductor", grantedAgents, StringComparer.Ordinal);
    }

    /// <summary>
    /// The parent-MCP sentence is emitted only when the derived grant is empty, so the receipt
    /// never claims Squad names no MCP tools while the agent's own list names eight.
    /// </summary>
    /// <remarks>
    /// The shipped squad grants every agent, so the empty branch is exercised against a corpus
    /// whose profile denies <c>filesystem.read</c>. On that corpus the agent's tools list carries
    /// no MCP name and the receipt has to say so; a granted agent's receipt must not repeat a
    /// claim the render contradicts.
    /// </remarks>
    [Fact]
    public async Task RenderAsync_CommandCode_StatesTheParentMcpGapOnlyWhenNothingIsNamed()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithFileReadDeniedAgent("review-lens");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        SquadAgent denied = Assert.Single(source.Agents, agent => agent.Name == "review-lens");
        Assert.True(
            SquadMcpGrantProjection.WithheldServers(source, denied, SquadTarget.CommandCode).Count > 0,
            "The fixture's read-denied agent is still entitled, so the empty-grant branch is not "
            + "exercised.");
        IReadOnlyList<string> kyberWeaveTools = SquadMcpGrantProjection.KyberWeaveToolNames(source);
        Assert.NotEmpty(kyberWeaveTools);

        SquadRenderResult result = await new SquadRendererRegistry([new CommandCodeRenderer()])
            .RenderAsync(new SquadRenderRequest(
                fixture.ProductRoot,
                [SquadTarget.CommandCode],
                SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        // The empty grant is the MCP tail, not the whole list: the agent keeps the built-in tools
        // its profile allows and names no MCP tool at all.
        IReadOnlyList<string> deniedTools = RenderedTools(result, denied.Name);
        Assert.DoesNotContain(deniedTools, tool => tool.StartsWith("mcp__", StringComparison.Ordinal));

        SquadDegradationRecord deniedRecord = Assert.Single(
            result.Degradations,
            d => string.Equals(d.CanonicalIdentity, denied.Name, StringComparison.Ordinal) &&
                 string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal));
        Assert.NotNull(deniedRecord.Details);
        Assert.Contains("MCP", deniedRecord.Details, StringComparison.Ordinal);

        foreach (SquadAgent agent in source.Agents.Where(agent =>
                     SquadMcpGrantProjection.GrantsKyberWeave(source, agent, SquadTarget.CommandCode)))
        {
            IReadOnlyList<string> tools = RenderedTools(result, agent.Name);
            Assert.All(
                kyberWeaveTools,
                tool => Assert.Contains(tool, tools, StringComparer.Ordinal));

            SquadDegradationRecord? record = result.Degradations.FirstOrDefault(d =>
                string.Equals(d.CanonicalIdentity, agent.Name, StringComparison.Ordinal) &&
                string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal));
            Assert.NotNull(record);
            Assert.NotNull(record.Details);

            // "Squad names none" is the falsehood the widening retires: the list above names the
            // roster's tools for exactly this agent.
            Assert.DoesNotContain("names none", record.Details, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SkillsRenderUnderCommandCodeSkillsAndNeverUnderAgentsCompatTree()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
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
        SquadSource source = TestSquadCorpus.CanonicalSource;
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
