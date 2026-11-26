using KyberWeave.Testing.Squad;
using System.Text;
using KyberWeave.Tests.Fakes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Corpus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source through <see cref="CommandCodeRenderer"/> and pins
/// the Command Code agent and skill contract verified against <c>command-code</c> 1.84.0.
/// </summary>
public sealed class CommandCodeRendererContractTests
{

    private static string ProductRoot => CanonicalSquadTree.Shared.ProductRoot;

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

    /// <summary>The server whose tools this target now names, and the one it must not start naming.</summary>
    private const string CodeGraphServer = "codegraph";

    private const string Context7Server = "context7";

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
        CanonicalSquadTree.Shared.Registry([new CommandCodeRenderer()]).RenderAsync(
            new SquadRenderRequest(ProductRoot, [SquadTarget.CommandCode], scope).Prepared(CanonicalSquadTree.Shared));

    private static Task<SquadRenderResult> RenderAsync(SquadDeploymentScope scope, SquadArbiterWiring? arbiter) =>
        CanonicalSquadTree.Shared.Registry([new CommandCodeRenderer()]).RenderAsync(
            new SquadRenderRequest(ProductRoot, [SquadTarget.CommandCode], scope, Arbiter: arbiter)
                .Prepared(CanonicalSquadTree.Shared));

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
            .. SquadMcpGrantProjection.GrantsOrchestratorExcludedServers(source, agent, SquadTarget.CommandCode)
                ? SquadMcpGrantProjection.QualifiedToolNames(
                    source,
                    server => string.Equals(server, CodeGraphServer, StringComparison.Ordinal))
                : [],
            .. SquadMcpGrantProjection.EntitledKyberWeaveToolNames(source, agent, SquadTarget.CommandCode)
        ];
    }

    /// <summary>
    /// Command Code names the <c>codegraph</c> tools for every agent the shared predicate entitles, and
    /// keeps the orchestrator out.
    /// </summary>
    /// <remarks>
    /// D26: Command Code reaches an MCP tool only when the subagent's own <c>tools</c> list names it, so a
    /// server this renderer stays silent about is a server the agent cannot use. The grant goes through
    /// <see cref="CapabilityDegradations.GrantsOrchestratorExcludedMcpServers" /> and the expectation here
    /// is computed from the same predicate rather than from a roster of agent names, so a profile change
    /// moves both sides together. <c>context7</c> is asserted absent beside it: the carve-out widening is
    /// for the code graph alone, and a target that started naming library lookups would be a second,
    /// unintended grant.
    /// </remarks>
    [Fact]
    public async Task CommandCodeNamesTheCodeGraphToolsForEveryEntitledAgentAndWithholdsContext7()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            IReadOnlyList<string> tools = RenderedTools(result, agent.Name);
            bool entitled =
                SquadMcpGrantProjection.GrantsOrchestratorExcludedServers(source, agent, SquadTarget.CommandCode);

            Assert.Equal(
                entitled,
                tools.Any(tool => tool.StartsWith($"mcp__{CodeGraphServer}__", StringComparison.Ordinal)));

            Assert.DoesNotContain(
                tools,
                tool => tool.StartsWith($"mcp__{Context7Server}__", StringComparison.Ordinal));
        }
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
    /// Every agent is granted the <c>kw-docs</c> roster by qualified name, so a subagent
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
        McpDeclarationSquadTree fixture =
            McpDeclarationSquadTree.WithFileReadDeniedAgent("review-lens");
        SquadSource source = fixture.Tree.Load();
        SquadAgent denied = Assert.Single(source.Agents, agent => agent.Name == "review-lens");
        Assert.True(
            SquadMcpGrantProjection.WithheldServers(source, denied, SquadTarget.CommandCode).Count > 0,
            "The fixture's read-denied agent is still entitled, so the empty-grant branch is not "
            + "exercised.");
        IReadOnlyList<string> kyberWeaveTools = SquadMcpGrantProjection.KyberWeaveToolNames(source);
        Assert.NotEmpty(kyberWeaveTools);

        SquadRenderResult result = await fixture.Tree.Registry([new CommandCodeRenderer()])
            .RenderAsync(new SquadRenderRequest(
                fixture.ProductRoot,
                [SquadTarget.CommandCode],
                SquadDeploymentScope.Project).Prepared(fixture.Tree));
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

    /// <summary>The owned Arbiter mod's project path: under the project's <c>.commandcode/</c> tree.</summary>
    private const string ArbiterModPath = ".commandcode/mods/kyber-arbiter.ts";

    private const int ArbiterHookTimeoutSeconds = 30;

    /// <summary>The mod body the render must emit byte for byte, pinned independently of the render.</summary>
    private static string ModBody =>
        CommandCodeRenderer.RenderArbiterMod(ArbiterHookTimeoutSeconds, SquadDeploymentScope.Project);

    [Fact]
    public async Task ArbiterEnabled_EmitsTheOwnedModAtTheProjectModsPath()
    {
        SquadRenderResult result = await RenderAsync(
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: ArbiterHookTimeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile mod = Assert.Single(result.Files, f => f.RelativePath == ArbiterModPath);
        Assert.Equal("commandcode", mod.Target);
        Assert.Equal(
            CommandCodeRenderer.RenderArbiterMod(ArbiterHookTimeoutSeconds, SquadDeploymentScope.Project),
            Encoding.UTF8.GetString(mod.Content.Span));
    }

    [Fact]
    public async Task ArbiterOmitted_EmitsNoModFile()
    {
        SquadRenderResult result = await RenderAsync(SquadDeploymentScope.Project, arbiter: null);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ArbiterModPath);
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("kyber-arbiter.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArbiterDisabled_StillEmitsTheModForTheDispatchHookSurface()
    {
        // The dispatch hook that expands a task id into the worker's prompt runs with the
        // Arbiter off, and the mod is the only surface that can carry it on Command Code.
        SquadRenderResult result = await RenderAsync(
            SquadDeploymentScope.Project,
            new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: ArbiterHookTimeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(result.Files, f => f.RelativePath == ArbiterModPath);
    }

    [Fact]
    public async Task InstanceScope_EmitsNoModFileEvenWhenTheArbiterIsEnabled()
    {
        // Command Code has no instance support, so the mod claims no instance scope: the
        // install paths that exist are the project mod and the user-scope mod.
        SquadRenderResult result = await RenderAsync(
            SquadDeploymentScope.Instance,
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: ArbiterHookTimeoutSeconds));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ArbiterModPath);
        Assert.DoesNotContain(result.Files, f => f.RelativePath.EndsWith("kyber-arbiter.ts", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderArbiterMod_DefaultExportsOneFactoryRegisteringBothHookPhases()
    {
        string body = ModBody;

        int defaultExport = body.IndexOf("export default", StringComparison.Ordinal);
        Assert.True(defaultExport >= 0, "the mod must default-export its factory");
        Assert.Equal(-1, body.IndexOf("export default", defaultExport + 1, StringComparison.Ordinal));
        Assert.Contains("cmd.hooks({", body, StringComparison.Ordinal);
        Assert.Contains("beforeToolCall:", body, StringComparison.Ordinal);
        Assert.Contains("afterToolCall:", body, StringComparison.Ordinal);
        // The mod host jiti-loads the file against no resolvable package, so a host type
        // import would fail the load; the ModApi surface is declared locally instead.
        Assert.DoesNotContain("@commandcode/harness", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderArbiterMod_FiltersOnTheAgentToolAndPassesEveryOtherToolThrough()
    {
        string body = ModBody;

        Assert.Contains("toolName !== \"agent\"", body, StringComparison.Ordinal);
        Assert.Contains("return undefined", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderArbiterMod_BuildsThePluginEventEnvelopeForBothPhases()
    {
        string body = ModBody;

        Assert.Contains("kyber-arbiter.plugin-event/v1", body, StringComparison.Ordinal);
        Assert.Contains("\"commandcode\"", body, StringComparison.Ordinal);
        Assert.Contains("\"before\"", body, StringComparison.Ordinal);
        Assert.Contains("\"after\"", body, StringComparison.Ordinal);
        Assert.Contains("\"call-id\"", body, StringComparison.Ordinal);
        Assert.Contains("session", body, StringComparison.Ordinal);
        Assert.Contains("cwd", body, StringComparison.Ordinal);
        Assert.Contains("args", body, StringComparison.Ordinal);
        Assert.Contains("result", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderArbiterMod_SpawnsTheArbiterHookThroughNodeChildProcessWithNoShell()
    {
        string body = ModBody;

        Assert.Contains("node:child_process", body, StringComparison.Ordinal);
        Assert.Contains("spawnSync", body, StringComparison.Ordinal);
        Assert.Contains("\"kyber-weave-arbiter\"", body, StringComparison.Ordinal);
        Assert.Contains("\"hook\"", body, StringComparison.Ordinal);
        Assert.Contains("\"--harness\"", body, StringComparison.Ordinal);
        Assert.Contains("\"commandcode\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("shell: true", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Bun.spawn", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderArbiterMod_BoundsTheSpawnByTheClampedTimeout()
    {
        Assert.Contains(
            "timeout: 30000",
            CommandCodeRenderer.RenderArbiterMod(30, SquadDeploymentScope.Project),
            StringComparison.Ordinal);
        Assert.Contains(
            "timeout: 0",
            CommandCodeRenderer.RenderArbiterMod(0, SquadDeploymentScope.Project),
            StringComparison.Ordinal);
        Assert.Contains(
            "timeout: 2147483647",
            CommandCodeRenderer.RenderArbiterMod(int.MaxValue, SquadDeploymentScope.Project),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RenderArbiterMod_MapsABlockDecisionToBlockWithAdditionalContext()
    {
        string body = ModBody;

        Assert.Contains("block: true", body, StringComparison.Ordinal);
        Assert.Contains("additionalContext", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderArbiterMod_AppliesStrippedArgsOntoTheToolInputSkippingPollutingKeys()
    {
        string body = ModBody;

        Assert.Contains("POLLUTING_ARG_KEYS", body, StringComparison.Ordinal);
        Assert.Contains("\"__proto__\"", body, StringComparison.Ordinal);
        Assert.Contains("\"constructor\"", body, StringComparison.Ordinal);
        Assert.Contains("\"prototype\"", body, StringComparison.Ordinal);
        int pollutingGuard = body.IndexOf("POLLUTING_ARG_KEYS.has(key)", StringComparison.Ordinal);
        int rewrite = body.IndexOf("input: rewritten", StringComparison.Ordinal);
        Assert.True(pollutingGuard >= 0, "the mod must guard the polluting keys");
        Assert.True(rewrite > pollutingGuard, "the input rewrite must apply the args after the polluting-key guard");
    }

    [Fact]
    public void RenderArbiterMod_FailsClosedOnSpawnFailureTimeoutNonZeroStatusAndUnparseableOutput()
    {
        string body = ModBody;

        Assert.Contains("result.error", body, StringComparison.Ordinal);
        Assert.Contains("timed out after", body, StringComparison.Ordinal);
        Assert.Contains("hook exited with status", body, StringComparison.Ordinal);
        Assert.Contains("malformed arbiter decision", body, StringComparison.Ordinal);
        Assert.Contains("failed to spawn kyber-weave-arbiter", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderArbiterMod_TreatsEmptyStdoutAsTheHostsDeliberateAllow()
    {
        string body = ModBody;

        // Exit 0 with empty stdout is the host's allow (arbiter.enabled: false, or a
        // dispatch with no target). JSON.parse("") would throw and wrongly block.
        int emptyCheck = body.IndexOf("trim() === \"\"", StringComparison.Ordinal);
        int parse = body.IndexOf("JSON.parse(", StringComparison.Ordinal);
        Assert.True(emptyCheck >= 0, "the mod must test for empty stdout");
        Assert.True(parse > emptyCheck, "the empty-stdout check must precede JSON.parse");
        Assert.Contains("{ decision: \"allow\" }", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderArbiterMod_CarriesTheGlobalScopeFlagOnlyAtGlobalScope()
    {
        Assert.DoesNotContain(
            "--scope global",
            CommandCodeRenderer.RenderArbiterMod(ArbiterHookTimeoutSeconds, SquadDeploymentScope.Project),
            StringComparison.Ordinal);
        Assert.Contains(
            ", \"--scope\", \"global\"",
            CommandCodeRenderer.RenderArbiterMod(ArbiterHookTimeoutSeconds, SquadDeploymentScope.Global),
            StringComparison.Ordinal);
    }
}
