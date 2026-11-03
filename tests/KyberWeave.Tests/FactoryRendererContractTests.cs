using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.RepresentationModel;
using static KyberWeave.Tests.Fakes.SquadRenderedOutput;

namespace KyberWeave.Tests;

/// <summary>
///     Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
///     <see cref="SquadRendererRegistry" /> with <see cref="FactoryRenderer" /> and validates the
///     result against Factory Droids' documented droid and skill path contracts
///     (docs.factory.ai, 2026-09-16).
/// </summary>
/// <remarks>
///     Validates that canonical agents render at <c>.factory/droids/&lt;name&gt;.md</c> (never
///     <c>.factory/agents/</c>) and skills at <c>.factory/skills/&lt;name&gt;/SKILL.md</c>. Asserts
///     against the loaded <see cref="SquadSource" /> model rather than hardcoded roster literals.
///     Pins the allow-only Factory tool-ID lowering independently of the renderer so omit-all
///     and <c>tools: all</c> cannot silently return.
/// </remarks>
public sealed class FactoryRendererContractTests : IDisposable
{
    /// <summary>
    ///     The verbatim sentence a droid carries when its rendered <c>mcpServers</c> is empty.
    ///     Keeping the empty case word-for-word is what makes the withholding visible and
    ///     distinguishable from a deliberate whitelist.
    /// </summary>
    private const string McpWithheldSentence =
        "mcpServers: [] excludes every MCP server so parent MCP was not inherited.";

    // Mirrors FactoryRenderer's allow-list (Factory accepts only these); update both together.
    private static readonly string[] AllowedReasoningEfforts = ["low", "medium", "high"];

    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    ///     Documented Factory custom-droid tool IDs (docs.factory.ai/harness/subagents,
    ///     2026-09-16). Auto-injected <c>TodoWrite</c> and <c>Skill</c> are not listed.
    ///     <c>ExitSpecMode</c> and <c>GenerateDroid</c> are forbidden. <c>Task</c> is not
    ///     available to subagents.
    /// </summary>
    private static readonly string[] DocumentedFactoryTools =
    [
        "Read",
        "LS",
        "Grep",
        "Glob",
        "Create",
        "Edit",
        "ApplyPatch",
        "Execute",
        "WebSearch",
        "FetchUrl"
    ];

    /// <summary>
    ///     Forbidden custom-droid tool IDs. Factory auto-injects TodoWrite/Skill; listing them
    ///     is incorrect. ExitSpecMode and GenerateDroid are validation errors. Task is withheld
    ///     from subagents.
    /// </summary>
    private static readonly string[] ForbiddenFactoryTools =
    [
        "TodoWrite",
        "Skill",
        "ExitSpecMode",
        "GenerateDroid",
        "Task",
        "all"
    ];

    /// <summary>
    ///     The capability→tool lowering this suite pins. Declared independently of the renderer
    ///     so a change to either side has to be made deliberately in both. Order is the fixed
    ///     emission order in D-capability-map. <c>network.publish</c> and <c>delegate</c> are
    ///     absent because Factory has no documented tool for either on a custom droid.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("filesystem.read", ["Read"]),
        ("filesystem.search", ["LS", "Grep", "Glob"]),
        ("filesystem.write", ["Create", "Edit", "ApplyPatch"]),
        ("process.execute", ["Execute"]),
        ("network.read", ["WebSearch", "FetchUrl"])
    ];

    /// <summary>Nothing to release: the suite only reads the checked-in corpus.</summary>
    public void Dispose()
    {
        // No disposable state: suite reads canonical checked-in corpus.
        // Implements IDisposable per repository test coding standard.
    }

    [Theory]
    [InlineData("glm-5.3[reasoningEffort=max]")]
    [InlineData("glm-5.3[reasoningEffort=high][reasoningEffort=low]")]
    [InlineData("[reasoningEffort=high]")]
    [InlineData(" [reasoningEffort=high]")]
    [InlineData("glm-5.3 [reasoningEffort=high]")]
    [InlineData("glm 5.3[reasoningEffort=high]")]
    [InlineData("glm-5.3[reasoningEffort=high")]
    public void ParseReasoningEffortSuffix_RejectsMalformedValues(string value)
    {
        Assert.Throws<SquadRenderValidationException>(() => FactoryRenderer.ParseReasoningEffortSuffix(value));
    }

    [Fact]
    public async Task RenderAsync_Factory_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        IReadOnlyList<string> declaredMcpServers = DeclaredFactoryMcpServers(source);
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f =>
            f.RelativePath.Contains(".factory/agents/", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase));

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".factory/droids/{agent.Name}.md");

            string text = Encoding.UTF8.GetString(file.Content.Span);
            Assert.DoesNotContain("tools: all", text, StringComparison.Ordinal);

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(text, agent.Name);

            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            Assert.Equal(agent.Description, RequireScalar(frontmatter, "description", agent.Name));

            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("permission")),
                $"Agent '{agent.Name}' should not emit 'permission'.");
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("permissions")),
                $"Agent '{agent.Name}' should not emit 'permissions'.");
            if (frontmatter.Children.ContainsKey(new YamlScalarNode("reasoningEffort")))
            {
                Assert.Contains(
                    RequireScalar(frontmatter, "reasoningEffort", agent.Name),
                    AllowedReasoningEfforts);
                Assert.True(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' emits 'reasoningEffort' without a pinned 'model' (Factory ignores it on inherit).");
            }

            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("license")),
                $"Agent '{agent.Name}' should not emit 'license' on a droid.");

            SquadCapabilityProfile capProfile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            IReadOnlyList<string> tools = RequireSequence(frontmatter, "tools", agent.Name);
            Assert.Equal(ExpectedFactoryTools(capProfile), tools);
            Assert.All(tools, tool =>
                Assert.Contains(tool, DocumentedFactoryTools, StringComparer.Ordinal));
            foreach (string forbidden in ForbiddenFactoryTools)
                Assert.DoesNotContain(forbidden, tools, StringComparer.Ordinal);

            // The whitelist is computed from the loaded source — the servers the squad's own
            // mcp.json declares plus the entitlement rule — never from the renderer. A blanket
            // mcpServers: [] satisfies an Assert.Empty here but not this.
            IReadOnlyList<string> expectedMcpServers =
                ExpectedFactoryMcpServers(source, agent, declaredMcpServers);
            IReadOnlyList<string> mcpServers = RequireSequence(frontmatter, "mcpServers", agent.Name);
            Assert.True(
                expectedMcpServers.SequenceEqual(mcpServers, StringComparer.Ordinal),
                $"Agent '{agent.Name}' (capability profile '{agent.CapabilityProfile}') rendered "
                + $"mcpServers [{string.Join(", ", mcpServers)}] but the declared servers plus the "
                + $"entitlement rule require [{string.Join(", ", expectedMcpServers)}].");
            Assert.All(
                mcpServers,
                server => Assert.Contains(
                    server,
                    declaredMcpServers,
                    StringComparer.Ordinal));

            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("factory", out string? factoryHarnessModel))
            {
                if (string.Equals(factoryHarnessModel, "inherit", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(factoryHarnessModel))
                {
                    Assert.False(
                        frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                        $"Agent '{agent.Name}' should omit 'model' (factory harness override is inherit/empty).");
                }
                else
                {
                    (string expectedModel, string? expectedEffort) =
                        FactoryRenderer.ParseReasoningEffortSuffix(factoryHarnessModel);
                    Assert.Equal(expectedModel, RequireScalar(frontmatter, "model", agent.Name));
                    if (expectedEffort is not null)
                        Assert.Equal(expectedEffort, RequireScalar(frontmatter, "reasoningEffort", agent.Name));
                    else
                        Assert.False(
                            frontmatter.Children.ContainsKey(new YamlScalarNode("reasoningEffort")),
                            $"Agent '{agent.Name}' should omit 'reasoningEffort' (no suffix on the factory model).");
                }
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(modelProfile.Default))
            {
                (string defaultModel, string? defaultEffort) =
                    FactoryRenderer.ParseReasoningEffortSuffix(modelProfile.Default);
                Assert.Equal(defaultModel, RequireScalar(frontmatter, "model", agent.Name));
                if (defaultEffort is not null)
                    Assert.Equal(defaultEffort, RequireScalar(frontmatter, "reasoningEffort", agent.Name));
                else
                    Assert.False(
                        frontmatter.Children.ContainsKey(new YamlScalarNode("reasoningEffort")),
                        $"Agent '{agent.Name}' should omit 'reasoningEffort' (profile default has no suffix).");
            }
            else
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' should omit 'model' (profile default is inherit).");
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("reasoningEffort")),
                    $"Agent '{agent.Name}' should omit 'reasoningEffort' (profile default is inherit).");
            }

            string expectedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedBody.EndsWith('\n')) expectedBody += "\n";

            Assert.Equal(expectedBody, body);
        }

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            SquadDegradationRecord[] agentRecords =
            [
                .. result.Degradations
                    .Where(d => string.Equals(d.CanonicalIdentity, agent.Name, StringComparison.Ordinal))
            ];

            string[] askCapabilities =
            [
                .. prof.Permissions
                    .Where(pair => pair.Value == SquadPermissionDecision.Ask)
                    .Select(pair => pair.Key)
                    .OrderBy(capability => capability, StringComparer.Ordinal)
            ];

            if (askCapabilities.Length > 0)
            {
                SquadDegradationRecord narrowed = Assert.Single(
                    agentRecords,
                    d => string.Equals(d.Code, "safety-narrowed", StringComparison.Ordinal));
                Assert.Equal("factory", narrowed.Target);
                Assert.Equal(agent.Name, narrowed.OutputIdentity);
                Assert.Equal(agent.BodyDigest, narrowed.InstructionDigest);
                Assert.NotNull(narrowed.Details);
                foreach (string capability in askCapabilities)
                    Assert.Contains(capability, narrowed.Details, StringComparison.Ordinal);

                AssertDoesNotClaimGuessedFactoryField(narrowed.Details, agent.Name);
            }
            else
            {
                Assert.DoesNotContain(
                    agentRecords,
                    d => string.Equals(d.Code, "safety-narrowed", StringComparison.Ordinal));
            }

            bool networkPublishNotExpressible =
                prof.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
                publishDecision != SquadPermissionDecision.Deny;
            bool delegateNotExpressible =
                prof.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                delegateDecision != SquadPermissionDecision.Deny;

            if (networkPublishNotExpressible)
                Assert.Contains(
                    agentRecords,
                    d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal) &&
                         d.Details is not null &&
                         d.Details.Contains("network.publish", StringComparison.Ordinal));

            if (delegateNotExpressible)
                Assert.Contains(
                    agentRecords,
                    d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal) &&
                         d.Details is not null &&
                         d.Details.Contains("delegate", StringComparison.Ordinal));

            foreach (SquadDegradationRecord degradation in agentRecords)
            {
                Assert.NotNull(degradation.Details);
                AssertDoesNotClaimGuessedFactoryField(degradation.Details, agent.Name);
            }
        }
    }

    /// <summary>
    ///     A droid entitled to read renders the servers the squad's own <c>mcp.json</c> declares;
    ///     every other droid renders the empty list. The key is always present and always a
    ///     sequence, so neither omission — which inherits the parent session's MCP tools — nor a
    ///     blanket <c>[]</c> can return.
    /// </summary>
    /// <remarks>
    ///     The expectation is computed from the loaded source model and never from the renderer.
    ///     That independence is the point: an assertion that reads the renderer's own answer
    ///     cannot disagree with it, which is how <c>mcpServers: []</c> survived for every droid
    ///     while the squad declared a server entitled roles could call.
    /// </remarks>
    [Fact]
    public async Task RenderAsync_Factory_WhitelistsDeclaredMcpServersForEntitledDroids()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        IReadOnlyList<string> declaredMcpServers = DeclaredFactoryMcpServers(source);
        Assert.NotEmpty(declaredMcpServers);

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            YamlMappingNode frontmatter = RenderedAgentFrontmatter(result, agent.Name);
            Assert.True(
                frontmatter.Children.ContainsKey(new YamlScalarNode("mcpServers")),
                $"Agent '{agent.Name}' omits 'mcpServers'. Omission inherits the parent session's "
                + "MCP tools, which is the widening the explicit key exists to prevent; the "
                + "entitled value is the declared servers, never an absent key.");
            IReadOnlyList<string> expected =
                ExpectedFactoryMcpServers(source, agent, declaredMcpServers);
            IReadOnlyList<string> actual = RequireSequence(frontmatter, "mcpServers", agent.Name);
            Assert.True(
                expected.SequenceEqual(actual, StringComparer.Ordinal),
                $"Agent '{agent.Name}' (capability profile '{agent.CapabilityProfile}') rendered "
                + $"mcpServers [{string.Join(", ", actual)}], but the declared servers plus the "
                + $"entitlement rule require [{string.Join(", ", expected)}].");
            Assert.All(
                actual,
                server => Assert.True(
                    declaredMcpServers.Contains(server, StringComparer.Ordinal),
                    $"Agent '{agent.Name}' renders mcpServers entry '{server}', which the squad's "
                    + "mcp.json does not declare. Squad does not invent Factory MCP server names."));
        }

        // The shipped claims: the read-entitled specialists carry the documentation server, and
        // so does the orchestrator — kyber-weave hands an agent the coding standards it is held
        // to, which a role that delegates work needs as much as the roles it delegates to.
        Assert.Equal(["codegraph", "kyber-weave"], RenderedMcpServers(result, "architect"));
        Assert.Equal(["codegraph", "kyber-weave"], RenderedMcpServers(result, "code-reviewer"));
        Assert.Equal(["codegraph", "kyber-weave"], RenderedMcpServers(result, "review-lens"));
        Assert.Equal(["kyber-weave"], RenderedMcpServers(result, "conductor"));

        // The orchestrator's grant is the rule, not the exception: assert it against the
        // source-computed entitlement so a re-added carve-out fails here as well as above.
        SquadAgent conductor = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        Assert.True(
            SquadMcpGrantProjection.GrantsKyberWeave(source, conductor, SquadTarget.Factory),
            "The source's own profile denies 'filesystem.read' to the orchestrator, so this suite "
            + "cannot state the conductor is entitled to kyber-weave.");
    }

    /// <summary>
    ///     The <c>permission-not-expressible</c> record reports MCP withholding exactly when the
    ///     rendered whitelist is empty, and exists exactly when it has something to express.
    /// </summary>
    /// <remarks>
    ///     The blanket sentence existed to make the withholding visible. A deliberately whitelisted
    ///     droid is a non-degraded outcome, so it carries no MCP claim — otherwise the two outcomes
    ///     are indistinguishable in the degradation stream, which is the only place a review or a
    ///     <c>squad doctor</c> run can see them without re-deriving the entitlement rule.
    /// </remarks>
    [Fact]
    public async Task RenderAsync_Factory_RecordsMcpWithholdingOnlyWhenTheWhitelistIsEmpty()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        IReadOnlyList<string> declaredMcpServers = DeclaredFactoryMcpServers(source);
        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            SquadDegradationRecord[] agentRecords =
            [
                .. result.Degradations
                    .Where(d => string.Equals(d.CanonicalIdentity, agent.Name, StringComparison.Ordinal))
            ];
            SquadDegradationRecord[] permissionRecords =
            [
                .. agentRecords
                    .Where(d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal))
            ];

            bool networkPublishNotExpressible =
                prof.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
                publishDecision != SquadPermissionDecision.Deny;
            bool delegateNotExpressible =
                prof.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                delegateDecision != SquadPermissionDecision.Deny;

            IReadOnlyList<string> expectedMcpServers =
                ExpectedFactoryMcpServers(source, agent, declaredMcpServers);

            if (expectedMcpServers.Count == 0)
            {
                SquadDegradationRecord withheld = Assert.Single(permissionRecords);
                Assert.Equal(agent.BodyDigest, withheld.InstructionDigest);
                Assert.Equal(agent.Name, withheld.OutputIdentity);
                Assert.NotNull(withheld.Details);
                Assert.Contains(McpWithheldSentence, withheld.Details, StringComparison.Ordinal);
                continue;
            }

            bool primaryAgentEntryPointNote =
                agent.Invocation == SquadInvocation.Primary &&
                string.Equals(
                    source.FallbackProfiles.Profiles[agent.Fallback].NoPrimaryAgent,
                    "skill",
                    StringComparison.Ordinal);

            if (networkPublishNotExpressible || delegateNotExpressible || primaryAgentEntryPointNote)
                Assert.Single(permissionRecords);
            else
                Assert.True(
                    permissionRecords.Length == 0,
                    $"Agent '{agent.Name}' rendered mcpServers "
                    + $"[{string.Join(", ", expectedMcpServers)}] and its profile raises no other "
                    + "inexpressible decision, so it must carry no permission-not-expressible "
                    + "record. Got: "
                    + string.Join(" | ", permissionRecords.Select(record => record.Details)));

            foreach (SquadDegradationRecord record in permissionRecords)
            {
                Assert.NotNull(record.Details);
                Assert.DoesNotContain(McpWithheldSentence, record.Details, StringComparison.Ordinal);
                Assert.DoesNotContain("parent MCP", record.Details, StringComparison.Ordinal);
            }

            // A subagent carries no entry-point caveat, so once its whitelist is non-empty
            // nothing in its degradations may mention MCP at all. The caveat on a primary agent
            // is the one place the value is named, and it is pinned separately.
            if (agent.Invocation != SquadInvocation.Primary)
                foreach (SquadDegradationRecord record in agentRecords)
                {
                    Assert.NotNull(record.Details);
                    Assert.DoesNotContain("mcpServers", record.Details, StringComparison.Ordinal);
                    Assert.DoesNotContain("MCP", record.Details, StringComparison.OrdinalIgnoreCase);
                }
        }
    }

    /// <summary>
    ///     A primary agent's entry-point caveat names the droid's actual <c>mcpServers</c> value.
    ///     The entry-point skill runs in the main session under the session's permissions, so the
    ///     caveat has to describe what the droid beside it really withholds — a caveat that names an
    ///     empty whitelist for a droid that carries one tells the reader the opposite of the truth.
    /// </summary>
    /// <remarks>
    ///     Every primary agent in the shipped squad is read-entitled, the conductor included, so the
    ///     non-empty form is the one the shipped corpus exercises. The empty form is pinned by
    ///     <see cref="RenderAsync_Factory_EmptyMcpDeclarationFailsClosedForEveryDroid" />.
    /// </remarks>
    [Fact]
    public async Task RenderAsync_Factory_EntryPointCaveatNamesTheDroidsRenderedMcpServers()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        IReadOnlyList<string> declaredMcpServers = DeclaredFactoryMcpServers(source);
        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        List<string> whitelistedPrimaries = [];
        foreach (SquadAgent agent in source.Agents.Where(candidate => candidate.Invocation == SquadInvocation.Primary))
        {
            IReadOnlyList<string> expected =
                ExpectedFactoryMcpServers(source, agent, declaredMcpServers);
            if (expected.Count == 0) continue;

            whitelistedPrimaries.Add(agent.Name);
            SquadDegradationRecord record = Assert.Single(
                result.Degradations,
                d => string.Equals(d.CanonicalIdentity, agent.Name, StringComparison.Ordinal) &&
                     string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal));
            Assert.NotNull(record.Details);
            Assert.Contains(
                $"mcpServers: [{string.Join(", ", expected)}]",
                record.Details,
                StringComparison.Ordinal);
            Assert.DoesNotContain("mcpServers: []", record.Details, StringComparison.Ordinal);
        }

        Assert.True(
            whitelistedPrimaries.Count > 0,
            "The shipped squad renders no whitelisted primary agent, so the entry-point caveat is "
            + "never exercised with a non-empty mcpServers value.");

        // The orchestrator is one of them: its caveat must name the server it carries, not the
        // empty list it used to.
        Assert.Contains("conductor", whitelistedPrimaries, StringComparer.Ordinal);
    }

    /// <summary>
    ///     The entry-point caveat's <c>mcpServers</c> fragment parses back to the droid's
    ///     whitelist, so the receipt cannot spell one server name as two.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_EntryPointCaveatMcpServersFragmentParsesAsTheWhitelist()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithServers("a, b");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        IReadOnlyList<string> declared = DeclaredFactoryMcpServers(source);
        Assert.Equal(["a, b"], declared);

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadAgent[] whitelistedPrimaries =
        [
            .. source.Agents.Where(agent => agent.Invocation == SquadInvocation.Primary &&
                                            ExpectedFactoryMcpServers(source, agent, declared).Count > 0)
        ];
        Assert.NotEmpty(whitelistedPrimaries);
        SquadAgent primary = whitelistedPrimaries[0];
        SquadDegradationRecord record = Assert.Single(
            result.Degradations,
            d => string.Equals(d.CanonicalIdentity, primary.Name, StringComparison.Ordinal) &&
                 string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal));
        Assert.NotNull(record.Details);

        Match fragment = Regex.Match(record.Details, @"mcpServers: (\[[^\]]*\])");
        Assert.True(
            fragment.Success,
            $"Agent '{primary.Name}' caveat does not name its mcpServers value: {record.Details}");

        YamlStream caveat = new();
        caveat.Load(new StringReader($"mcpServers: {fragment.Groups[1].Value}"));
        YamlMappingNode caveatFrontmatter = Assert.IsType<YamlMappingNode>(caveat.Documents[0].RootNode);
        Assert.Equal(declared, RequireSequence(caveatFrontmatter, "mcpServers", primary.Name));
    }

    /// <summary>
    ///     A squad that declares no MCP server at all fails closed: every droid renders
    ///     <c>mcpServers: []</c> and every droid keeps the withholding sentence, so an empty
    ///     declaration can never turn into a grant.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_EmptyMcpDeclarationFailsClosedForEveryDroid()
    {
        using McpDeclarationCorpusFixture fixture = McpDeclarationCorpusFixture.WithServers();
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        Assert.Empty(DeclaredFactoryMcpServers(source));
        Assert.NotEmpty(source.Agents);

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            YamlMappingNode frontmatter = RenderedAgentFrontmatter(result, agent.Name);
            Assert.Empty(RequireSequence(frontmatter, "mcpServers", agent.Name));

            SquadDegradationRecord withheld = Assert.Single(
                result.Degradations,
                d => string.Equals(d.CanonicalIdentity, agent.Name, StringComparison.Ordinal) &&
                     string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal));
            Assert.NotNull(withheld.Details);
            Assert.Contains(McpWithheldSentence, withheld.Details, StringComparison.Ordinal);

            // A primary agent's entry-point caveat names the empty value too: with no server
            // declared there is nothing for the caveat to point at, and the caveat still has to
            // describe the droid beside it.
            if (agent.Invocation == SquadInvocation.Primary &&
                string.Equals(
                    source.FallbackProfiles.Profiles[agent.Fallback].NoPrimaryAgent,
                    "skill",
                    StringComparison.Ordinal))
                Assert.Contains("mcpServers: []", withheld.Details, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     An agent whose capability profile cannot be resolved is refused before anything renders:
    ///     the pipeline fails closed on an unresolvable profile for every harness (ADR 0056), rather
    ///     than rendering an agent whose grants it cannot derive.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_RefusesAnAgentWhoseCapabilityProfileIsUnresolved()
    {
        string agentName = "review-lens";
        SquadSource canonical = TestSquadCorpus.CanonicalSource;
        Assert.Contains(canonical.Agents, agent => agent.Name == agentName);
        SquadSource source = canonical with
        {
            Agents =
            [
                .. canonical.Agents.Select(agent =>
                    string.Equals(agent.Name, agentName, StringComparison.Ordinal)
                        ? agent with { CapabilityProfile = "unresolved-profile" }
                        : agent)
            ]
        };

        SquadSourceValidationException refused = await Assert.ThrowsAsync<SquadSourceValidationException>(() =>
            new FactoryRenderer().RenderAsync(new SquadRenderRequest(
                ProductRoot,
                [SquadTarget.Factory],
                SquadDeploymentScope.Project,
                PreloadedSource: source)));
        Assert.Contains(agentName, refused.Message, StringComparison.Ordinal);
        Assert.Contains("unresolved-profile", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A declared server name that contains YAML flow indicators still renders as exactly one
    ///     entry, so the frontmatter cannot invent names the squad never declared.
    /// </summary>
    /// <remarks>
    ///     The value is YAML the Factory parser reads, not a display string. A plain scalar spelled
    ///     inside a flow sequence treats <c>,</c>, <c>[</c>, and <c>]</c> as structure, so a name
    ///     such as <c>a,b</c> would silently become two servers unless the emitter is allowed to
    ///     quote it.
    /// </remarks>
    [Fact]
    public async Task RenderAsync_Factory_McpServerNamesWithFlowIndicatorsStayOneEntry()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithServers("a,b", "bracket[server]", "plain");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        IReadOnlyList<string> declared = DeclaredFactoryMcpServers(source);
        Assert.Equal(["a,b", "bracket[server]", "plain"], declared);

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadAgent entitled = Assert.Single(source.Agents, agent => agent.Name == "architect");
        Assert.Equal(declared, RenderedMcpServers(result, entitled.Name));
    }

    /// <summary>
    ///     A multi-server declaration renders in ordinal order regardless of the order
    ///     <c>mcp.json</c> happens to declare it in, so a rendered droid is byte-stable across an
    ///     edit that only reorders the declaration.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_OrdersTheWhitelistOrdinalWhateverTheDeclarationOrder()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithServers("zeta-reader", "alpha-reader");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        IReadOnlyList<string> declared = DeclaredFactoryMcpServers(source);
        Assert.Equal(["alpha-reader", "zeta-reader"], declared);

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        int whitelistedDroids = 0;
        foreach (SquadAgent agent in source.Agents)
        {
            IReadOnlyList<string> expected =
                ExpectedFactoryMcpServers(source, agent, declared);
            IReadOnlyList<string> actual = RenderedMcpServers(result, agent.Name);
            if (expected.Count > 0) whitelistedDroids++;

            Assert.True(
                expected.SequenceEqual(actual, StringComparer.Ordinal),
                $"Agent '{agent.Name}' (capability profile '{agent.CapabilityProfile}') rendered "
                + $"mcpServers [{string.Join(", ", actual)}], but the declared servers plus the "
                + $"entitlement rule require [{string.Join(", ", expected)}] in ordinal order.");
        }

        Assert.True(
            whitelistedDroids > 0,
            "No droid is entitled to the declared servers, so nothing about the emission order "
            + "was exercised.");
    }

    /// <summary>
    ///     A profile that denies <c>filesystem.read</c> withholds the whitelist while a read-allowed
    ///     sibling keeps it, so the read-entitlement clause is load-bearing rather than a
    ///     restatement of the shipped corpus.
    /// </summary>
    /// <remarks>
    ///     Every profile in the shipped squad allows <c>filesystem.read</c>, which leaves the clause
    ///     indistinguishable from "every profile is entitled": dropping it from the renderer, or
    ///     narrowing a profile to deny reads, would keep the rest of this suite green while a role
    ///     whose own profile withholds file reads was handed a documentation server it has no grant
    ///     to use. This case is what makes the clause fail a build instead.
    /// </remarks>
    [Fact]
    public async Task RenderAsync_Factory_WithholdsTheWhitelistWhenTheProfileDeniesFileRead()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithFileReadDeniedAgent("review-lens");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        IReadOnlyList<string> declared = DeclaredFactoryMcpServers(source);
        Assert.NotEmpty(declared);

        SquadAgent denied = Assert.Single(source.Agents, agent => agent.Name == "review-lens");
        SquadAgent entitled = Assert.Single(source.Agents, agent => agent.Name == "architect");
        Assert.Equal(
            SquadPermissionDecision.Deny,
            source.CapabilityProfiles.Profiles[denied.CapabilityProfile].Permissions["filesystem.read"]);
        Assert.Equal(
            SquadPermissionDecision.Allow,
            source.CapabilityProfiles.Profiles[entitled.CapabilityProfile].Permissions["filesystem.read"]);
        Assert.Empty(ExpectedFactoryMcpServers(source, denied, declared));
        Assert.Equal(declared, ExpectedFactoryMcpServers(source, entitled, declared));

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        Assert.Empty(RenderedMcpServers(result, denied.Name));
        Assert.Equal(declared, RenderedMcpServers(result, entitled.Name));

        SquadDegradationRecord withheld = Assert.Single(
            result.Degradations,
            d => string.Equals(d.CanonicalIdentity, denied.Name, StringComparison.Ordinal) &&
                 string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal));
        Assert.NotNull(withheld.Details);
        Assert.Contains(McpWithheldSentence, withheld.Details, StringComparison.Ordinal);

        foreach (SquadAgent agent in source.Agents)
        {
            IReadOnlyList<string> expected = ExpectedFactoryMcpServers(source, agent, declared);
            IReadOnlyList<string> actual = RenderedMcpServers(result, agent.Name);
            Assert.True(
                expected.SequenceEqual(actual, StringComparer.Ordinal),
                $"Agent '{agent.Name}' (capability profile '{agent.CapabilityProfile}') rendered "
                + $"mcpServers [{string.Join(", ", actual)}], but the declared servers plus the "
                + $"entitlement rule require [{string.Join(", ", expected)}].");
        }
    }
    /// <summary>
    ///     The Factory app dispatches custom droids as subagents and exposes no session-parent
    ///     selector, so the primary agent renders as both a droid (kept for Task dispatch) and an
    ///     entry-point skill carrying the canonical agent body and its resource closure, under
    ///     exactly one role-skill-fallback record.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_ExposesThePrimaryAgentAsAnEntryPointSkillBesideItsDroid()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Name == "conductor");
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Droid file is present.
        Assert.Single(
            result.Files,
            f => f.RelativePath == $".factory/droids/{primaryAgent.Name}.md");

        // Entry-point skill is present with the Factory skill frontmatter: {name, description}.
        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".factory/skills/{primaryAgent.Name}/SKILL.md");
        (YamlMappingNode skillFrontmatter, string skillBody) = SplitFrontmatter(
            Encoding.UTF8.GetString(skillFile.Content.Span),
            primaryAgent.Name);
        Assert.Equal(
            ["name", "description"],
            skillFrontmatter.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(n => n.Value)
                .ToArray());
        Assert.Equal(primaryAgent.Name, RequireScalar(skillFrontmatter, "name", primaryAgent.Name));
        string expectedDescription = string.Join(" ", primaryAgent.Description.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.Equal(expectedDescription, RequireScalar(skillFrontmatter, "description", primaryAgent.Name));

        // Body matches the canonical agent body.
        string expectedAgentBody = primaryAgent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!expectedAgentBody.EndsWith('\n')) expectedAgentBody += "\n";
        Assert.Equal(expectedAgentBody, skillBody);

        // Resources are projected beside the skill.
        foreach (SquadResource resource in primaryAgent.Resources)
        {
            SquadDeploymentFile resourceFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".factory/skills/{primaryAgent.Name}/{resource.RelativePath}");
            ReadOnlySpan<byte> expectedBytes = Encoding.UTF8.GetBytes(resource.Content);
            Assert.True(expectedBytes.SequenceEqual(resourceFile.Content.Span));
        }

        // Exactly one role-skill-fallback record, naming the entry-point skill and the profile.
        SquadDegradationRecord roleSkillFallback = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "role-skill-fallback");
        Assert.Equal("factory", roleSkillFallback.Target);
        Assert.Equal(primaryAgent.Name, roleSkillFallback.OutputIdentity);
        Assert.Equal(primaryAgent.BodyDigest, roleSkillFallback.InstructionDigest);
        Assert.Contains(
            $".factory/skills/{primaryAgent.Name}/SKILL.md",
            roleSkillFallback.Details,
            StringComparison.Ordinal);
        Assert.Contains(primaryAgent.Fallback, roleSkillFallback.Details, StringComparison.Ordinal);
        Assert.Contains("no-primary-agent: skill", roleSkillFallback.Details, StringComparison.Ordinal);

        // The shared permission-not-expressible record states the entry-point caveat.
        SquadDegradationRecord permissionNotExpressible = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "permission-not-expressible");
        Assert.Contains(
            $".factory/skills/{primaryAgent.Name}/SKILL.md",
            permissionNotExpressible.Details,
            StringComparison.Ordinal);

        // The orchestrator profile yields exactly these two codes (no asks to narrow, and
        // process.execute/filesystem.write are denied, so no capability-not-isolable record).
        Assert.Equal(
            ["permission-not-expressible", "role-skill-fallback"],
            result.Degradations
                .Where(d => d.CanonicalIdentity == primaryAgent.Name)
                .Select(d => d.Code)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal));

        // All relative Markdown links in the skill body resolve to emitted files.
        const string linkPattern = @"\[([^\]]+)\]\(([^)]+)\)";
        foreach (Match match in Regex.Matches(skillBody, linkPattern))
        {
            string link = match.Groups[2].Value;
            if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !link.StartsWith('#'))
            {
                string expectedPath = Path.Combine(
                    $".factory/skills/{primaryAgent.Name}",
                    link).Replace("\\", "/", StringComparison.Ordinal);
                Assert.Contains(result.Files, f => f.RelativePath == expectedPath);
            }
        }

        // At most one record per (Target, CanonicalIdentity, Code), and no widening anywhere.
        List<(string Target, string CanonicalIdentity, string Code)> recordKeys = result.Degradations
            .Select(d => (d.Target, d.CanonicalIdentity, d.Code))
            .ToList();
        Assert.Equal(recordKeys.Count, recordKeys.Distinct().Count());
        Assert.DoesNotContain(
            result.Degradations,
            d => d.Details != null && d.Details.Contains("widening", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Global scope renders the primary agent entry-point skill under <c>skills/</c> instead
    ///     of <c>.factory/skills/</c>, and the droid under <c>droids/</c>.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_GlobalScopePlacesTheEntryPointSkillUnderSkills()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Name == "conductor");
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Global);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Skill file and resources are under skills/ without the .factory prefix.
        Assert.Single(
            result.Files,
            f => f.RelativePath == $"skills/{primaryAgent.Name}/SKILL.md");
        foreach (SquadResource resource in primaryAgent.Resources)
            Assert.Single(
                result.Files,
                f => f.RelativePath == $"skills/{primaryAgent.Name}/{resource.RelativePath}");

        // Droid file is under droids/ without the .factory prefix.
        Assert.Single(
            result.Files,
            f => f.RelativePath == $"droids/{primaryAgent.Name}.md");

        // Degradation records name the skill at its global-scope path, not the
        // project-scope form.
        SquadDegradationRecord roleSkillFallback = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "role-skill-fallback");
        Assert.Contains(
            $"skills/{primaryAgent.Name}/SKILL.md",
            roleSkillFallback.Details,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".factory/", roleSkillFallback.Details, StringComparison.Ordinal);
        SquadDegradationRecord permissionNotExpressible = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "permission-not-expressible");
        Assert.Contains(
            $"skills/{primaryAgent.Name}/SKILL.md",
            permissionNotExpressible.Details,
            StringComparison.Ordinal);

        Assert.DoesNotContain(result.Files, f => f.RelativePath.StartsWith(".factory/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The no-primary-agent setting switches only the entry-point skill: omit mode keeps the
    ///     droid byte-identical, emits nothing under the entry-point skill path, and records no
    ///     role-skill-fallback.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_NoPrimaryAgentValueSwitchesOnlyTheEntryPointSkill()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Name == "conductor");
        SquadRendererRegistry registry = new([new FactoryRenderer()]);

        SquadRenderRequest skillRequest = new(
            ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project);
        SquadRenderResult skillResult = await registry.RenderAsync(skillRequest);
        Assert.True(skillResult.Success);

        using (ClaudeNoPrimaryAgentFixture omitFixture = ClaudeNoPrimaryAgentFixture.Create("omit"))
        {
            SquadRenderRequest omitRequest = new(
                omitFixture.ProductRoot,
                [SquadTarget.Factory],
                SquadDeploymentScope.Project);
            SquadRenderResult omitResult = await registry.RenderAsync(omitRequest);
            Assert.True(omitResult.Success, string.Join("; ", omitResult.Errors));

            // Omit mode emits nothing under the entry-point skill path.
            Assert.DoesNotContain(
                omitResult.Files,
                f => f.RelativePath.StartsWith($".factory/skills/{primaryAgent.Name}/", StringComparison.Ordinal));

            // All omit-mode paths appear in skill mode.
            foreach (SquadDeploymentFile omitFile in omitResult.Files)
                Assert.Contains(skillResult.Files, sf => sf.RelativePath == omitFile.RelativePath);

            // Skill-mode paths minus omit-mode paths are exactly the skill and its resources.
            HashSet<string> skillPaths = new(skillResult.Files.Select(f => f.RelativePath));
            HashSet<string> omitPaths = new(omitResult.Files.Select(f => f.RelativePath));
            skillPaths.ExceptWith(omitPaths);
            string[] expectedSkillPaths = new string[1 + primaryAgent.Resources.Count];
            expectedSkillPaths[0] = $".factory/skills/{primaryAgent.Name}/SKILL.md";
            for (int i = 0; i < primaryAgent.Resources.Count; i++)
                expectedSkillPaths[i + 1] =
                    $".factory/skills/{primaryAgent.Name}/{primaryAgent.Resources[i].RelativePath}";
            Assert.Equal(
                expectedSkillPaths.OrderBy(p => p, StringComparer.Ordinal),
                skillPaths.OrderBy(p => p, StringComparer.Ordinal));

            // Droid bytes are identical across the two modes.
            SquadDeploymentFile skillDroidFile = Assert.Single(
                skillResult.Files,
                f => f.RelativePath == $".factory/droids/{primaryAgent.Name}.md");
            SquadDeploymentFile omitDroidFile = Assert.Single(
                omitResult.Files,
                f => f.RelativePath == $".factory/droids/{primaryAgent.Name}.md");
            Assert.True(skillDroidFile.Content.Span.SequenceEqual(omitDroidFile.Content.Span));

            // Omit mode records no role-skill-fallback and no entry-point caveat.
            Assert.DoesNotContain(
                omitResult.Degradations,
                d => d.CanonicalIdentity == primaryAgent.Name && d.Code == "role-skill-fallback");
            Assert.All(
                omitResult.Degradations.Where(d => d.CanonicalIdentity == primaryAgent.Name),
                d => Assert.DoesNotContain(
                    $".factory/skills/{primaryAgent.Name}",
                    d.Details ?? string.Empty,
                    StringComparison.Ordinal));
        }
    }

    /// <summary>
    ///     The renderer throws when a canonical skill has the same name as the primary agent,
    ///     before any file is produced: Factory renders both a droid and an entry-point skill, so
    ///     the collision cannot be resolved by role-prefixed fallback.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_ThrowsWhenACanonicalSkillOccupiesTheEntryPointIdentity()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Name == "conductor");

        using (PiPrimaryIdentityCollisionFixture collisionFixture =
               PiPrimaryIdentityCollisionFixture.Create(primaryAgent.Name))
        {
            FactoryRenderer renderer = new();
            SquadRenderRequest request = new(
                collisionFixture.ProductRoot,
                [SquadTarget.Factory],
                SquadDeploymentScope.Project);

            SquadRenderValidationException ex =
                await Assert.ThrowsAsync<SquadRenderValidationException>(() => renderer.RenderAsync(request));
            Assert.Contains(primaryAgent.Name, ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Regression guard: the loader rejects unsupported no-primary-agent values before the
    ///     renderer runs, so an unknown value fails closed rather than silently skipping the
    ///     entry-point skill.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_FailsClosedOnAnUnsupportedNoPrimaryAgentValue()
    {
        using (ClaudeNoPrimaryAgentFixture renameFixture = ClaudeNoPrimaryAgentFixture.Create("rename"))
        {
            FactoryRenderer renderer = new();
            SquadRenderRequest request = new(
                renameFixture.ProductRoot,
                [SquadTarget.Factory],
                SquadDeploymentScope.Project);

            SquadSourceValidationException ex =
                await Assert.ThrowsAsync<SquadSourceValidationException>(() => renderer.RenderAsync(request));
            Assert.Contains("no-primary-agent", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RenderSkill_FrontmatterKeyOrderIsStable()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> sharedIdentities = SharedIdentities(source);
        string probe = source.Skills
                           .Select(skill => skill.Name)
                           .Where(name => !sharedIdentities.Contains(name))
                           .OrderBy(name => name, StringComparer.Ordinal)
                           .FirstOrDefault()
                       ?? throw new InvalidOperationException(
                           $"Corpus at '{ProductRoot}' declares no non-suppressed skills.");

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [SquadTarget.Factory],
            SquadDeploymentScope.Project));

        SquadDeploymentFile rendered = Assert.Single(
            result.Files,
            f => f.RelativePath == $".factory/skills/{probe}/SKILL.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(rendered.Content.Span),
            probe);

        string[] keys = frontmatter.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["name", "description"], keys);
    }

    /// <summary>
    ///     Independently computed allow-only Factory tool IDs in D-capability-map order.
    ///     Only <see cref="SquadPermissionDecision.Allow" /> grants a tool.
    /// </summary>
    private static IReadOnlyList<string> ExpectedFactoryTools(SquadCapabilityProfile profile)
    {
        List<string> granted = [];
        foreach ((string capability, string[] tools) in CapabilityToolContract)
            if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision == SquadPermissionDecision.Allow)
                granted.AddRange(tools);

        return granted;
    }

    /// <summary>
    ///     The <c>mcpServers</c> value a droid must render: every declared server when the agent's
    ///     capability profile allows <c>filesystem.read</c>, otherwise the empty list.
    /// </summary>
    /// <remarks>
    ///     The entitlement is read from <see cref="SquadMcpGrantProjection" /> rather than from the
    ///     renderer, so the test states the contract instead of echoing the implementation. The
    ///     orchestrator is not carved out of it: <c>kyber-weave</c> carries
    ///     <c>agent_coding_standards</c>, and the conductor has to hand its delegates the standards
    ///     they are held to.
    /// </remarks>
    private static IReadOnlyList<string> ExpectedFactoryMcpServers(
        SquadSource source,
        SquadAgent agent,
        IReadOnlyList<string> declaredServers)
    {
        bool grantsUniversal = SquadMcpGrantProjection.GrantsKyberWeave(source, agent, SquadTarget.Factory);
        bool grantsOrchestratorExcluded = CapabilityDegradations.GrantsOrchestratorExcludedMcpServers(
            agent.CapabilityProfile,
            source.CapabilityProfiles.Profiles);

        List<string> granted = [];
        foreach (string server in declaredServers)
            if (string.Equals(server, CapabilityDegradations.UniversalMcpServer, StringComparison.Ordinal))
            {
                if (grantsUniversal) granted.Add(server);
            }
            else
            {
                if (grantsOrchestratorExcluded) granted.Add(server);
            }

        return granted;
    }

    /// <summary>The rendered droid's frontmatter, located by the identity the agent names.</summary>
    private static YamlMappingNode RenderedAgentFrontmatter(SquadRenderResult result, string agentName)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            candidate => candidate.RelativePath == $".factory/droids/{agentName}.md");
        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(file.Content.Span),
            agentName);
        return frontmatter;
    }

    /// <summary>The <c>mcpServers</c> sequence a rendered droid's frontmatter carries.</summary>
    private static IReadOnlyList<string> DeclaredFactoryMcpServers(SquadSource source)
    {
        return
        [
            .. SquadMcpDeclaration.ServerNames(source)
                .Where(name => !string.Equals(name, "kyber-weave-arbiter", StringComparison.Ordinal))
        ];
    }

    private static IReadOnlyList<string> RenderedMcpServers(SquadRenderResult result, string agentName) =>
        RequireSequence(RenderedAgentFrontmatter(result, agentName), "mcpServers", agentName);

    private static void AssertDoesNotClaimGuessedFactoryField(string details, string identity)
    {
        Assert.DoesNotContain("guessed", details, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            details.Contains("permission:", StringComparison.Ordinal) ||
            details.Contains("permissions:", StringComparison.Ordinal),
            $"Agent '{identity}' degradation details claim a guessed Factory field: {details}");
    }

    private static IReadOnlyList<string> RequireSequence(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        YamlSequenceNode sequence = Assert.IsType<YamlSequenceNode>(value);
        return sequence.Children
            .Select(child => Assert.IsType<YamlScalarNode>(child).Value
                             ?? throw new InvalidOperationException(
                                 $"'{identity}' key '{key}' has a null sequence entry."))
            .ToArray();
    }
}

/// <summary>
///     Copies the real <c>products/kyber-squad</c> corpus and replaces its <c>mcp.json</c> server
///     declaration with the one a test names, so the declaration is the only variable.
/// </summary>
/// <remarks>
///     A copy rather than an edit keeps the canonical corpus as the thing under test: everything
///     except the one variable a variant names stays real, including the agent roster the whitelist
///     is projected onto. <see cref="WithServers" /> keeps the declaration order the caller passes,
///     so a test can tell ordinal emission from insertion order.
/// </remarks>
internal sealed class McpDeclarationCorpusFixture : IDisposable
{
    /// <summary>
    ///     The profile <see cref="WithFileReadDeniedAgent" /> points its agent at: the corpus's own
    ///     <c>read-only</c> profile with <c>filesystem.read</c> flipped to deny.
    /// </summary>
    private const string FileReadDeniedProfile = "read-only-no-file-read";

    private const string ReadOnlyProfile = "read-only";

    /// <summary>
    ///     The primary agent the pure-orchestrator wording is asserted on. It is the only shipped
    ///     agent whose capability profile is the orchestrator, and a primary agent is the only one
    ///     whose degradation receipt carries the entry-point-skill note.
    /// </summary>
    private const string ConductorAgent = "conductor";

    /// <summary>
    ///     The Copilot roster a read-denied orchestrator can still justify: the three tools the
    ///     catalog marks ungoverned and the one its <c>delegate: allow</c> covers. The shipped
    ///     roster also names read, search, and network tools, and the source validator rejects any
    ///     of those against a profile that denies the capability it needs.
    /// </summary>
    private const string ReadDeniedOrchestratorCopilotTools =
        "copilot-tools: [vscode/toolSearch, vscode/askQuestions, todo, agent]";

    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { WriteIndented = true };

    private readonly TempDirectory _temp = new();

    private McpDeclarationCorpusFixture()
    {
        ProductRoot = Path.Combine(_temp.Path, "kyber-squad");
    }

    internal string ProductRoot { get; }

    public void Dispose()
    {
        _temp.Dispose();
    }

    internal static McpDeclarationCorpusFixture WithServers(params string[] serverNames)
    {
        McpDeclarationCorpusFixture fixture = CopyCanonical();

        Dictionary<string, Dictionary<string, object>> servers = new(StringComparer.Ordinal);
        foreach (string name in serverNames)
            servers[name] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["command"] = $"{name}-mcp",
                ["args"] = new[] { "--repo-root", "." }
            };

        Dictionary<string, Dictionary<string, Dictionary<string, object>>> root =
            new(StringComparer.Ordinal) { ["mcpServers"] = servers };
        File.WriteAllText(
            Path.Combine(fixture.ProductRoot, "mcp.json"),
            JsonSerializer.Serialize(root, JsonSerializerOptions) + "\n",
            new UTF8Encoding(false));

        return fixture;
    }

    /// <summary>
    ///     Copies the canonical corpus, adds
    ///     <see cref="FileReadDeniedProfile" /> as a clone of <c>read-only</c> with
    ///     <c>filesystem.read: deny</c>, and points <paramref name="agentName" /> at it.
    /// </summary>
    /// <remarks>
    ///     The profile is a clone rather than a fresh declaration so the read decision is the only
    ///     difference from a profile the corpus already ships — the test then isolates the
    ///     entitlement clause instead of also changing search, execute, and delegation. The Copilot
    ///     roster is narrowed with it because the source validator checks every Copilot tool against
    ///     the shared profile, and a read-denied profile cannot justify a read tool.
    /// </remarks>
    internal static McpDeclarationCorpusFixture WithFileReadDeniedAgent(string agentName)
    {
        McpDeclarationCorpusFixture fixture = CopyCanonical();
        fixture.AddReadDeniedVariantOf(ReadOnlyProfile);
        fixture.RepointAgent(agentName);
        return fixture;
    }

    internal static McpDeclarationCorpusFixture WithServersAndFileReadDeniedAgent(
        string agentName,
        params string[] serverNames)
    {
        McpDeclarationCorpusFixture fixture = WithServers(serverNames);
        fixture.AddReadDeniedVariantOf(ReadOnlyProfile);
        fixture.RepointAgent(agentName);
        return fixture;
    }

    /// <summary>
    ///     Copies the canonical corpus and denies <c>filesystem.read</c> in the corpus's own
    ///     <c>orchestrator</c> profile, leaving every profile name intact.
    /// </summary>
    /// <remarks>
    ///     Denied in place rather than through a clone in the manner of
    ///     <see cref="WithFileReadDeniedAgent" />, because the withholding note a renderer states keys on
    ///     the profile's identity as well as on its permissions: an agent repointed at a
    ///     differently-named read-denied profile is no longer a pure orchestrator, so the branch that
    ///     names the withholding is never reached and a test built on it would assert nothing.
    ///     The conductor's Copilot roster is narrowed with it. The source validator checks every
    ///     Copilot tool against the shared profile, so a read-denied orchestrator cannot keep the read,
    ///     search, or network tools the shipped roster declares and the source would not load at all.
    /// </remarks>
    internal static McpDeclarationCorpusFixture WithFileReadDeniedOrchestrator()
    {
        McpDeclarationCorpusFixture fixture = CopyCanonical();
        fixture.DenyFileReadInProfile(SquadMcpGrantProjection.PureOrchestratorProfile);
        fixture.NarrowCopilotTools(ConductorAgent, ReadDeniedOrchestratorCopilotTools);
        return fixture;
    }

    private static McpDeclarationCorpusFixture CopyCanonical()
    {
        McpDeclarationCorpusFixture fixture = new();
        PiCorpusFixtureHelpers.CopyDirectory(
            Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad"),
            fixture.ProductRoot);
        return fixture;
    }

    private void AddReadDeniedVariantOf(string sourceProfileName)
    {
        string path = Path.Combine(ProductRoot, "profiles", "capabilities.yml");
        string[] lines = File.ReadAllLines(path);

        (int start, int end) = LocateProfileBlock(lines, sourceProfileName, path);

        List<string> clone = [.. lines[start..end]];
        clone[0] = $"  {FileReadDeniedProfile}:";

        bool rewroteRead = false;
        for (int index = 0; index < clone.Count; index++)
            if (clone[index].TrimStart().StartsWith("filesystem.read:", StringComparison.Ordinal))
            {
                clone[index] = "      filesystem.read: deny";
                rewroteRead = true;
            }

        if (!rewroteRead)
            throw new InvalidOperationException(
                $"Profile '{sourceProfileName}' in '{path}' declares no 'filesystem.read' permission "
                + $"to deny, so '{FileReadDeniedProfile}' would not differ from it.");

        File.WriteAllText(
            path,
            File.ReadAllText(path).TrimEnd('\n') + "\n" + string.Join("\n", clone) + "\n",
            new UTF8Encoding(false));
    }

    /// <summary>
    ///     Rewrites the <c>filesystem.read</c> decision of <paramref name="profileName" /> to
    ///     <c>deny</c> inside that profile's own block, so the profile keeps its identity and loses
    ///     one permission.
    /// </summary>
    private void DenyFileReadInProfile(string profileName)
    {
        string path = Path.Combine(ProductRoot, "profiles", "capabilities.yml");
        string[] lines = File.ReadAllLines(path);

        (int start, int end) = LocateProfileBlock(lines, profileName, path);

        int rewritten = 0;
        for (int index = start; index < end; index++)
            if (lines[index].TrimStart().StartsWith("filesystem.read:", StringComparison.Ordinal))
            {
                lines[index] = "      filesystem.read: deny";
                rewritten++;
            }

        // Exactly one, not at least one: rewriting a second read decision would deny the
        // permission somewhere the test did not intend, and the fixture would then differ from
        // the canonical corpus in two places instead of the one it names.
        if (rewritten != 1)
            throw new InvalidOperationException(
                $"Profile '{profileName}' in '{path}' declares {rewritten} 'filesystem.read' "
                + "permissions; in-place denial needs exactly one.");

        File.WriteAllText(
            path,
            string.Join("\n", lines) + "\n",
            new UTF8Encoding(false));
    }

    /// <summary>
    ///     The half-open line range of one profile block: its <c>  &lt;name&gt;:</c> header up to the
    ///     next profile header, with trailing blank lines trimmed so a clone does not carry the
    ///     separator that belongs between blocks.
    /// </summary>
    private static (int Start, int End) LocateProfileBlock(
        string[] lines,
        string profileName,
        string path)
    {
        int start = Array.IndexOf(lines, $"  {profileName}:");
        if (start < 0)
            throw new InvalidOperationException(
                $"Expected '{path}' to declare profile '{profileName}'.");

        int end = start + 1;
        while (end < lines.Length && !IsProfileHeader(lines[end])) end++;

        while (end > start + 1 && string.IsNullOrWhiteSpace(lines[end - 1])) end--;

        return (start, end);
    }

    private void RepointAgent(string agentName)
    {
        string path = Path.Combine(ProductRoot, "agents", $"{agentName}.md");
        string text = File.ReadAllText(path);

        string repointed = Regex.Replace(
            text,
            "^capability-profile: .*$",
            $"capability-profile: {FileReadDeniedProfile}",
            RegexOptions.Multiline);
        if (string.Equals(repointed, text, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Expected '{path}' to declare a 'capability-profile' line to repoint.");

        File.WriteAllText(
            path,
            Narrowed(repointed, path, "copilot-tools: [vscode, todo, search, web]"),
            new UTF8Encoding(false));
    }

    /// <summary>
    ///     Replaces the agent's <c>copilot-tools</c> line with the one the caller supplies.
    /// </summary>
    /// <remarks>
    ///     A read-denied profile cannot justify the read, search, or network tools an agent declares,
    ///     and <c>SquadSourceValidator.ValidateCopilotToolCapabilities</c> rejects the mismatch at load
    ///     time — so every variant that denies a capability has to narrow the Copilot roster to match,
    ///     or the render under test never gets as far as running.
    /// </remarks>
    private void NarrowCopilotTools(string agentName, string tools)
    {
        string path = Path.Combine(ProductRoot, "agents", $"{agentName}.md");
        File.WriteAllText(
            path,
            Narrowed(File.ReadAllText(path), path, tools),
            new UTF8Encoding(false));
    }

    private static string Narrowed(string text, string path, string tools)
    {
        string narrowed = Regex.Replace(text, "^copilot-tools: .*$", tools, RegexOptions.Multiline);
        if (string.Equals(narrowed, text, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Expected '{path}' to declare a 'copilot-tools' line to narrow.");

        return narrowed;
    }

    /// <summary>
    ///     A profile entry sits at the mapping's own indent: two spaces, then a non-space, ending
    ///     with a colon. Every nested line is indented further, so this separates one block from the
    ///     next without a YAML parse of a file the loader will parse anyway.
    /// </summary>
    private static bool IsProfileHeader(string line)
    {
        return line.Length > 2
               && line[0] == ' '
               && line[1] == ' '
               && line[2] != ' '
               && line[^1] == ':';
    }
}
