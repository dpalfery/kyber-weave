using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="FactoryRenderer"/> and validates the
/// result against Factory Droids' documented droid and skill path contracts
/// (docs.factory.ai, 2026-09-16).
/// </summary>
/// <remarks>
/// Validates that canonical agents render at <c>.factory/droids/&lt;name&gt;.md</c> (never
/// <c>.factory/agents/</c>) and skills at <c>.factory/skills/&lt;name&gt;/SKILL.md</c>. Asserts
/// against the loaded <see cref="SquadSource"/> model rather than hardcoded roster literals.
/// Pins the allow-only Factory tool-ID lowering independently of the renderer so omit-all
/// and <c>tools: all</c> cannot silently return.
/// </remarks>
public sealed class FactoryRendererContractTests : IDisposable
{
    // Mirrors FactoryRenderer's allow-list (Factory accepts only these); update both together.
    private static readonly string[] AllowedReasoningEfforts = ["low", "medium", "high"];

    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Documented Factory custom-droid tool IDs (docs.factory.ai/harness/subagents,
    /// 2026-09-16). Auto-injected <c>TodoWrite</c> and <c>Skill</c> are not listed.
    /// <c>ExitSpecMode</c> and <c>GenerateDroid</c> are forbidden. <c>Task</c> is not
    /// available to subagents.
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
    /// Forbidden custom-droid tool IDs. Factory auto-injects TodoWrite/Skill; listing them
    /// is incorrect. ExitSpecMode and GenerateDroid are validation errors. Task is withheld
    /// from subagents.
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
    /// The capability→tool lowering this suite pins. Declared independently of the renderer
    /// so a change to either side has to be made deliberately in both. Order is the fixed
    /// emission order in D-capability-map. <c>network.publish</c> and <c>delegate</c> are
    /// absent because Factory has no documented tool for either on a custom droid.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("filesystem.read", ["Read"]),
        ("filesystem.search", ["LS", "Grep", "Glob"]),
        ("filesystem.write", ["Create", "Edit", "ApplyPatch"]),
        ("process.execute", ["Execute"]),
        ("network.read", ["WebSearch", "FetchUrl"]),
    ];

    /// <summary>
    /// Shared identities are read from the loaded fallback profile so the test follows the
    /// same generic single-projection contract as the renderer.
    /// </summary>
    private static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Nothing to release: the suite only reads the checked-in corpus.</summary>
    public void Dispose()
    {
        // No disposable state: suite reads canonical checked-in corpus.
        // Implements IDisposable per repository test coding standard.
    }

    [Fact]
    public void SupportedTargets_IsExactlyFactory()
    {
        SquadRendererRegistry registry = new([new FactoryRenderer()]);

        Assert.Equal([SquadTarget.Factory], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonFactoryTarget()
    {
        FactoryRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
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
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));
        int expectedSkillCount = source.Skills.Count - suppressedSkillCount;

        // The primary agent also projects an entry-point skill (1 + its resource closure)
        // beside its droid, per the native dual entry point.
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);
        int primaryAgentSkillContribution = 1 + primaryAgent.Resources.Count;

        int expectedFileCount = source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + expectedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count)
            + primaryAgentSkillContribution;
        Assert.Equal(expectedFileCount, result.Files.Count);

        Assert.All(result.Files, f =>
        {
            Assert.Equal("factory", f.Target);
            Assert.True(
                f.RelativePath.StartsWith(".factory/droids/", StringComparison.Ordinal) ||
                f.RelativePath.StartsWith(".factory/skills/", StringComparison.Ordinal),
                $"File '{f.RelativePath}' does not start with expected Factory directory.");
        });

        Assert.DoesNotContain(result.Files, f =>
            f.RelativePath.Contains(".factory/agents/", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase));

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);

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
            {
                Assert.DoesNotContain(forbidden, tools, StringComparer.Ordinal);
            }

            IReadOnlyList<string> mcpServers = RequireSequence(frontmatter, "mcpServers", agent.Name);
            Assert.Empty(mcpServers);

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
                    (string expectedModel, string? expectedEffort) = FactoryRenderer.ParseReasoningEffortSuffix(factoryHarnessModel);
                    Assert.Equal(expectedModel, RequireScalar(frontmatter, "model", agent.Name));
                    if (expectedEffort is not null)
                    {
                        Assert.Equal(expectedEffort, RequireScalar(frontmatter, "reasoningEffort", agent.Name));
                    }
                    else
                    {
                        Assert.False(
                            frontmatter.Children.ContainsKey(new YamlScalarNode("reasoningEffort")),
                            $"Agent '{agent.Name}' should omit 'reasoningEffort' (no suffix on the factory model).");
                    }
                }
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(modelProfile.Default))
            {
                (string defaultModel, string? defaultEffort) = FactoryRenderer.ParseReasoningEffortSuffix(modelProfile.Default);
                Assert.Equal(defaultModel, RequireScalar(frontmatter, "model", agent.Name));
                if (defaultEffort is not null)
                {
                    Assert.Equal(defaultEffort, RequireScalar(frontmatter, "reasoningEffort", agent.Name));
                }
                else
                {
                    Assert.False(
                        frontmatter.Children.ContainsKey(new YamlScalarNode("reasoningEffort")),
                        $"Agent '{agent.Name}' should omit 'reasoningEffort' (profile default has no suffix).");
                }
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
            if (!expectedBody.EndsWith('\n'))
            {
                expectedBody += "\n";
            }

            Assert.Equal(expectedBody, body);
        }

        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".factory/droids/{sharedIdentity}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".factory/skills/{sharedIdentity}/SKILL.md");
        }

        foreach (SquadSkill skill in source.Skills)
        {
            bool isSharedIdentity = sharedIdentities.Contains(skill.Name);
            string path = $".factory/skills/{skill.Name}/SKILL.md";
            if (isSharedIdentity)
            {
                Assert.DoesNotContain(result.Files, f => f.RelativePath == path);
                continue;
            }

            SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == path);
            (YamlMappingNode frontmatter, string skillBody) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                skill.Name);

            Assert.Equal(skill.Name, RequireScalar(frontmatter, "name", skill.Name));

            string expectedDescription = string.Join(" ", skill.Description.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.Equal(expectedDescription, RequireScalar(frontmatter, "description", skill.Name));
            Assert.DoesNotContain(frontmatter.Children.Keys.OfType<YamlScalarNode>(), key =>
                string.Equals(key.Value, "license", StringComparison.Ordinal));

            string expectedSkillBody = skill.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedSkillBody.EndsWith('\n'))
            {
                expectedSkillBody += "\n";
            }

            Assert.Equal(expectedSkillBody, skillBody);
        }

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            SquadDegradationRecord[] agentRecords = [.. result.Degradations
                .Where(d => string.Equals(d.CanonicalIdentity, agent.Name, StringComparison.Ordinal))];

            string[] askCapabilities = [.. prof.Permissions
                .Where(pair => pair.Value == SquadPermissionDecision.Ask)
                .Select(pair => pair.Key)
                .OrderBy(capability => capability, StringComparer.Ordinal)];

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
                {
                    Assert.Contains(capability, narrowed.Details, StringComparison.Ordinal);
                }

                AssertDoesNotClaimGuessedFactoryField(narrowed.Details, agent.Name);
            }
            else
            {
                Assert.DoesNotContain(
                    agentRecords,
                    d => string.Equals(d.Code, "safety-narrowed", StringComparison.Ordinal));
            }

            if (prof.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
                publishDecision != SquadPermissionDecision.Deny)
            {
                Assert.Contains(
                    agentRecords,
                    d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal) &&
                         d.Details is not null &&
                         d.Details.Contains("network.publish", StringComparison.Ordinal));
            }

            if (prof.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                delegateDecision != SquadPermissionDecision.Deny)
            {
                Assert.Contains(
                    agentRecords,
                    d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal) &&
                         d.Details is not null &&
                         d.Details.Contains("delegate", StringComparison.Ordinal));
            }

            SquadDegradationRecord mcpRecord = Assert.Single(
                agentRecords,
                d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal) &&
                     d.Details is not null &&
                     (d.Details.Contains("mcpServers", StringComparison.Ordinal) ||
                      d.Details.Contains("MCP", StringComparison.Ordinal)));
            Assert.Equal(agent.BodyDigest, mcpRecord.InstructionDigest);
            Assert.Equal(agent.Name, mcpRecord.OutputIdentity);

            foreach (SquadDegradationRecord degradation in agentRecords)
            {
                Assert.Equal("factory", degradation.Target);
                Assert.Equal(agent.Name, degradation.OutputIdentity);
                Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
                Assert.NotNull(degradation.Details);
                AssertDoesNotClaimGuessedFactoryField(degradation.Details, agent.Name);
            }
        }

        Assert.NotEmpty(result.Degradations);
        Assert.All(
            result.Degradations,
            d => Assert.True(
                agentsByName.ContainsKey(d.CanonicalIdentity),
                $"Degradation names unknown identity '{d.CanonicalIdentity}'."));
    }

    /// <summary>
    /// Agents with process.execute: allow and filesystem.write: ask or deny (e.g. investigator and
    /// reviewer profiles) receive a capability-not-isolable degradation record naming the granted
    /// shell tools (Execute) and withheld write tools (ApplyPatch, Create, Edit).
    /// Agents with filesystem.write: allow or process.execute: deny do not receive this degradation.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_RecordsCapabilityNotIsolableForShellImpliesWrite()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        string[] grantedShellTools = ["Execute"];
        string[] withheldWriteTools = ["ApplyPatch", "Create", "Edit"];

        List<string> expectedAgents = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile p = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                bool exec = p.Permissions.TryGetValue("process.execute", out SquadPermissionDecision e) && e == SquadPermissionDecision.Allow;
                bool write = p.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision w) && w == SquadPermissionDecision.Allow;
                return exec && !write;
            })
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expectedAgents);

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            bool executeAllowed = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec) &&
                exec == SquadPermissionDecision.Allow;
            bool writeAllowed = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write) &&
                write == SquadPermissionDecision.Allow;

            SquadDegradationRecord? record = result.Degradations.FirstOrDefault(
                d => d.CanonicalIdentity == agent.Name && d.Code == "capability-not-isolable");

            if (executeAllowed && !writeAllowed)
            {
                Assert.NotNull(record);
                Assert.Equal("factory", record.Target);
                Assert.Equal(agent.Name, record.CanonicalIdentity);
                Assert.Equal(agent.Name, record.OutputIdentity);
                Assert.Equal(agent.BodyDigest, record.InstructionDigest);
                Assert.NotNull(record.Details);
                foreach (string shellTool in grantedShellTools)
                {
                    Assert.Contains(shellTool, record.Details, StringComparison.Ordinal);
                }
                foreach (string writeTool in withheldWriteTools)
                {
                    Assert.Contains(writeTool, record.Details, StringComparison.Ordinal);
                }
            }
            else
            {
                Assert.Null(record);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Factory_GlobalScope_StripsFactoryPrefix()
    {
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Global);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotEmpty(result.Files);
        Assert.All(result.Files, f =>
        {
            Assert.Equal("factory", f.Target);
            Assert.False(
                f.RelativePath.StartsWith('.'),
                $"Global-scope path '{f.RelativePath}' still carries a project-scope dot-prefix.");
            Assert.DoesNotContain(".factory", f.RelativePath, StringComparison.Ordinal);
            Assert.True(
                f.RelativePath.StartsWith("droids/", StringComparison.Ordinal) ||
                f.RelativePath.StartsWith("skills/", StringComparison.Ordinal),
                $"Global-scope path '{f.RelativePath}' is neither droids/ nor skills/.");
        });

        Assert.DoesNotContain(result.Files, f =>
            f.RelativePath.Contains("agents/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The Factory app dispatches custom droids as subagents and exposes no session-parent
    /// selector, so the primary agent renders as both a droid (kept for Task dispatch) and an
    /// entry-point skill carrying the canonical agent body and its resource closure, under
    /// exactly one role-skill-fallback record.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_ExposesThePrimaryAgentAsAnEntryPointSkillBesideItsDroid()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);

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
        if (!expectedAgentBody.EndsWith('\n'))
        {
            expectedAgentBody += "\n";
        }
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
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(skillBody, linkPattern))
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
        var recordKeys = result.Degradations
            .Select(d => (d.Target, d.CanonicalIdentity, d.Code))
            .ToList();
        Assert.Equal(recordKeys.Count, recordKeys.Distinct().Count());
        Assert.DoesNotContain(
            result.Degradations,
            d => d.Details != null && d.Details.Contains("widening", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Global scope renders the primary agent entry-point skill under <c>skills/</c> instead
    /// of <c>.factory/skills/</c>, and the droid under <c>droids/</c>.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_GlobalScopePlacesTheEntryPointSkillUnderSkills()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Global);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Skill file and resources are under skills/ without the .factory prefix.
        Assert.Single(
            result.Files,
            f => f.RelativePath == $"skills/{primaryAgent.Name}/SKILL.md");
        foreach (SquadResource resource in primaryAgent.Resources)
        {
            Assert.Single(
                result.Files,
                f => f.RelativePath == $"skills/{primaryAgent.Name}/{resource.RelativePath}");
        }

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
    /// The no-primary-agent setting switches only the entry-point skill: omit mode keeps the
    /// droid byte-identical, emits nothing under the entry-point skill path, and records no
    /// role-skill-fallback.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_NoPrimaryAgentValueSwitchesOnlyTheEntryPointSkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);
        SquadRendererRegistry registry = new([new FactoryRenderer()]);

        SquadRenderRequest skillRequest = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);
        SquadRenderResult skillResult = await registry.RenderAsync(skillRequest);
        Assert.True(skillResult.Success);

        using (var omitFixture = ClaudeNoPrimaryAgentFixture.Create("omit"))
        {
            SquadRenderRequest omitRequest = new(
                SourceDirectory: omitFixture.ProductRoot,
                Targets: [SquadTarget.Factory],
                Scope: SquadDeploymentScope.Project);
            SquadRenderResult omitResult = await registry.RenderAsync(omitRequest);
            Assert.True(omitResult.Success, string.Join("; ", omitResult.Errors));

            // Omit mode emits nothing under the entry-point skill path.
            Assert.DoesNotContain(
                omitResult.Files,
                f => f.RelativePath.StartsWith($".factory/skills/{primaryAgent.Name}/", StringComparison.Ordinal));

            // All omit-mode paths appear in skill mode.
            foreach (SquadDeploymentFile omitFile in omitResult.Files)
            {
                Assert.Contains(skillResult.Files, sf => sf.RelativePath == omitFile.RelativePath);
            }

            // Skill-mode paths minus omit-mode paths are exactly the skill and its resources.
            HashSet<string> skillPaths = new(skillResult.Files.Select(f => f.RelativePath));
            HashSet<string> omitPaths = new(omitResult.Files.Select(f => f.RelativePath));
            skillPaths.ExceptWith(omitPaths);
            string[] expectedSkillPaths = new string[1 + primaryAgent.Resources.Count];
            expectedSkillPaths[0] = $".factory/skills/{primaryAgent.Name}/SKILL.md";
            for (int i = 0; i < primaryAgent.Resources.Count; i++)
            {
                expectedSkillPaths[i + 1] = $".factory/skills/{primaryAgent.Name}/{primaryAgent.Resources[i].RelativePath}";
            }
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
    /// The renderer throws when a canonical skill has the same name as the primary agent,
    /// before any file is produced: Factory renders both a droid and an entry-point skill, so
    /// the collision cannot be resolved by role-prefixed fallback.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_ThrowsWhenACanonicalSkillOccupiesTheEntryPointIdentity()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primaryAgent = Assert.Single(source.Agents,
            a => a.Invocation == SquadInvocation.Primary);

        using (var collisionFixture = PiPrimaryIdentityCollisionFixture.Create(primaryAgent.Name))
        {
            FactoryRenderer renderer = new();
            SquadRenderRequest request = new(
                SourceDirectory: collisionFixture.ProductRoot,
                Targets: [SquadTarget.Factory],
                Scope: SquadDeploymentScope.Project);

            SquadRenderValidationException ex = await Assert.ThrowsAsync<SquadRenderValidationException>(
                () => renderer.RenderAsync(request));
            Assert.Contains(primaryAgent.Name, ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Regression guard: the loader rejects unsupported no-primary-agent values before the
    /// renderer runs, so an unknown value fails closed rather than silently skipping the
    /// entry-point skill.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Factory_FailsClosedOnAnUnsupportedNoPrimaryAgentValue()
    {
        using (var renameFixture = ClaudeNoPrimaryAgentFixture.Create("rename"))
        {
            FactoryRenderer renderer = new();
            SquadRenderRequest request = new(
                SourceDirectory: renameFixture.ProductRoot,
                Targets: [SquadTarget.Factory],
                Scope: SquadDeploymentScope.Project);

            SquadSourceValidationException ex = await Assert.ThrowsAsync<SquadSourceValidationException>(
                () => renderer.RenderAsync(request));
            Assert.Contains("no-primary-agent", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RenderAsync_IsDeterministic()
    {
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult first = await registry.RenderAsync(request);
        SquadRenderResult second = await registry.RenderAsync(request);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Files.Count, second.Files.Count);

        for (int i = 0; i < first.Files.Count; i++)
        {
            SquadDeploymentFile file1 = first.Files[i];
            SquadDeploymentFile file2 = second.Files[i];

            Assert.Equal(file1.RelativePath, file2.RelativePath);
            Assert.Equal(file1.Target, file2.Target);
            Assert.True(file1.Content.Span.SequenceEqual(file2.Content.Span), $"Content differed for file '{file1.RelativePath}'.");
        }

        Assert.Equal(first.Degradations.Count, second.Degradations.Count);
        for (int i = 0; i < first.Degradations.Count; i++)
        {
            Assert.Equal(first.Degradations[i], second.Degradations[i]);
        }
    }

    [Fact]
    public async Task RenderAsync_Factory_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new FactoryRenderer(),
            SquadTarget.Factory,
            ".factory/droids/bug-crusher-investigator.md",
            ".factory/droids",
            ".factory/skills");
    }

    [Fact]
    public async Task RenderSkill_FrontmatterKeyOrderIsStable()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);
        string probe = source.Skills
            .Select(skill => skill.Name)
            .Where(name => !sharedIdentities.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Corpus at '{ProductRoot}' declares no non-suppressed skills.");

        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project));

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
    /// Independently computed allow-only Factory tool IDs in D-capability-map order.
    /// Only <see cref="SquadPermissionDecision.Allow"/> grants a tool.
    /// </summary>
    private static IReadOnlyList<string> ExpectedFactoryTools(SquadCapabilityProfile profile)
    {
        List<string> granted = [];
        foreach ((string capability, string[] tools) in CapabilityToolContract)
        {
            if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision == SquadPermissionDecision.Allow)
            {
                granted.AddRange(tools);
            }
        }

        return granted;
    }

    private static void AssertDoesNotClaimGuessedFactoryField(string details, string identity)
    {
        Assert.DoesNotContain("guessed", details, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            details.Contains("permission:", StringComparison.Ordinal) ||
            details.Contains("permissions:", StringComparison.Ordinal),
            $"Agent '{identity}' degradation details claim a guessed Factory field: {details}");
    }

    private static (YamlMappingNode Frontmatter, string Body) SplitFrontmatter(string text, string identity)
    {
        const string delimiter = "---\n";
        Assert.True(
            text.StartsWith(delimiter, StringComparison.Ordinal),
            $"'{identity}' is missing the opening frontmatter delimiter.");
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, $"'{identity}' is missing a closing '---' frontmatter delimiter.");

        string yaml = text[delimiter.Length..(end + 1)];
        string body = text[(end + 5)..];

        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
        return (root, body);
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
                ?? throw new InvalidOperationException($"'{identity}' key '{key}' has a null sequence entry."))
            .ToArray();
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
