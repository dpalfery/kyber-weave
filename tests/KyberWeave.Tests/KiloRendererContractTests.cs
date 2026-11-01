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
///     Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
///     <see cref="SquadRendererRegistry" /> with <see cref="KiloRenderer" /> and validates the result
///     against Kilo's native agent and skill contracts.
/// </summary>
/// <remarks>
///     Validates that canonical agents lower to Kilo agents at <c>.kilo/agents/&lt;name&gt;.md</c>
///     and skills lower to <c>.kilo/skills/&lt;name&gt;/SKILL.md</c>. Verifies profile-declared
///     shared-identity suppression, model resolution from <c>models.yml</c>, deterministic output,
///     and structured degradation accounting (<c>permission-not-expressible</c> with matching
///     <c>InstructionDigest</c>, plus <c>resource-links-rewritten</c> for the discovery guard).
/// </remarks>
public sealed class KiloRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>Nothing to release: the suite only reads the checked-in corpus.</summary>
    public void Dispose()
    {
        // No disposable state: suite reads canonical checked-in corpus.
    }

    /// <summary>
    ///     Shared identities are read from the loaded fallback profile so the test follows the
    ///     same generic single-projection contract as the renderer.
    /// </summary>
    private static HashSet<string> SharedIdentities(SquadSource source)
    {
        return source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    ///     The shared identities whose canonical skill is suppressed: those carried by a subagent,
    ///     whose file already holds the identical body. A primary shared identity reuses its
    ///     canonical skill as the entry point, so it is emitted. Delegated to the renderer's own
    ///     resolver so the test cannot drift from the contract.
    /// </summary>
    private static HashSet<string> SuppressedSkillIdentities(SquadSource source)
    {
        return SquadSharedIdentityProjection.SuppressedSkillIdentities(source, SharedIdentities(source));
    }

    [Fact]
    public void SupportedTargets_IsExactlyKilo()
    {
        SquadRendererRegistry registry = new([new KiloRenderer()]);

        Assert.Equal([SquadTarget.Kilo], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Claude, SquadTarget.Kilo],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonKiloTarget()
    {
        KiloRenderer renderer = new();
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Kilo_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> sharedIdentities = SharedIdentities(source);
        HashSet<string> suppressedSkillIdentities = SuppressedSkillIdentities(source);
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Kilo],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => suppressedSkillIdentities.Contains(skill.Name));
        int expectedSkillCount = source.Skills.Count - suppressedSkillCount;

        // C3: every rendered owner also projects its validated resource closure beside its
        // principal output, so the corpus count is principals plus emitted closures.
        int expectedFileCount =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
                                + expectedSkillCount
                                + source.Skills.Where(skill => !suppressedSkillIdentities.Contains(skill.Name))
                                    .Sum(skill => skill.Resources.Count);
        Assert.Equal(expectedFileCount, result.Files.Count);

        Assert.All(result.Files, f =>
        {
            Assert.Equal("kilo", f.Target);
            Assert.True(
                f.RelativePath.StartsWith(".kilo/agents/", StringComparison.Ordinal) ||
                f.RelativePath.StartsWith(".kilo/skills/", StringComparison.Ordinal),
                $"File '{f.RelativePath}' does not start with expected Kilo directory.");
        });

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        int inheritedModelAgentCount = 0;
        int concreteModelAgentCount = 0;

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".kilo/agents/{agent.Name}.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent.Name);
            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            Assert.True(
                string.Equals(agent.Description, RequireScalar(frontmatter, "description", agent.Name),
                    StringComparison.Ordinal),
                $"Agent '{agent.Name}' description mismatch.");

            string expectedMode = agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent";
            Assert.Equal(expectedMode, RequireScalar(frontmatter, "mode", agent.Name));

            // Model resolution: verify against loaded ModelProfiles.
            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("kilo", out string? expectedModel))
            {
                if (string.Equals(expectedModel, "inherit", StringComparison.Ordinal))
                {
                    inheritedModelAgentCount++;
                    Assert.False(
                        frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                        $"Agent '{agent.Name}' should omit 'model' when resolved to inherit.");
                }
                else
                {
                    concreteModelAgentCount++;
                    Assert.True(
                        string.Equals(expectedModel, RequireScalar(frontmatter, "model", agent.Name),
                            StringComparison.Ordinal),
                        $"Agent '{agent.Name}' model mismatch: expected '{expectedModel}'.");
                }
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal))
            {
                concreteModelAgentCount++;
                Assert.True(
                    string.Equals(modelProfile.Default, RequireScalar(frontmatter, "model", agent.Name),
                        StringComparison.Ordinal),
                    $"Agent '{agent.Name}' model mismatch: expected '{modelProfile.Default}'.");
            }
            else
            {
                inheritedModelAgentCount++;
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' should omit 'model' (profile default is inherit).");
            }

            // Exact comparison with the discovery-guarded body: in-closure .md
            // resource links are rewritten to .markdown so they still resolve beside
            // their principal; every other byte stays verbatim.
            string verbatimBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!verbatimBody.EndsWith('\n')) verbatimBody += "\n";

            string expectedAgentBody = verbatimBody;
            foreach (SquadResource resource in agent.Resources)
                if (resource.RelativePath.EndsWith(".md", StringComparison.Ordinal))
                    expectedAgentBody = expectedAgentBody.Replace(
                        resource.RelativePath,
                        string.Concat(resource.RelativePath[..^".md".Length], ".markdown"),
                        StringComparison.Ordinal);

            Assert.True(
                string.Equals(expectedAgentBody, body, StringComparison.Ordinal),
                $"Agent '{agent.Name}' body mismatch.");

            // Pin the guard link-by-link, matching SquadResourceRenderingContract:
            // every authored in-closure .md link appears rewritten, and the original
            // target is gone.
            foreach (SquadResource resource in agent.Resources)
            {
                if (!resource.RelativePath.EndsWith(".md", StringComparison.Ordinal) ||
                    !verbatimBody.Contains(resource.RelativePath, StringComparison.Ordinal))
                    continue;

                string rewrittenTarget = string.Concat(resource.RelativePath[..^".md".Length], ".markdown");
                Assert.Contains(rewrittenTarget, body, StringComparison.Ordinal);
                Assert.DoesNotContain(resource.RelativePath, body, StringComparison.Ordinal);
            }
        }

        // Canonical model resolution verification:
        // 'conductor' uses the orchestration profile (kilo: inherit) and must omit 'model'.
        // All non-conductor agents configure concrete kilo models and must emit them.
        Assert.Equal(1, inheritedModelAgentCount);
        Assert.Equal(source.Agents.Count - 1, concreteModelAgentCount);

        SquadDeploymentFile conductorFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".kilo/agents/conductor.md");
        (YamlMappingNode conductorFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(conductorFile.Content.Span),
            "conductor");
        Assert.False(
            conductorFrontmatter.Children.ContainsKey(new YamlScalarNode("model")),
            "Agent 'conductor' must omit 'model' key in frontmatter because orchestration has kilo: inherit.");

        SquadDeploymentFile architectFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".kilo/agents/architect.md");
        (YamlMappingNode architectFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(architectFile.Content.Span),
            "architect");
        Assert.Equal(
            "kilo/deepseek/deepseek-v4-pro-0813",
            RequireScalar(architectFrontmatter, "model", "architect"));

        // Shared identities suppression verification
        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".kilo/agents/{sharedIdentity}.md");
            string sharedSkillPath = $".kilo/skills/{sharedIdentity}/SKILL.md";
            if (suppressedSkillIdentities.Contains(sharedIdentity))
                Assert.DoesNotContain(result.Files, f => f.RelativePath == sharedSkillPath);
            else
                // Primary shared identity: the canonical skill is the entry point and ships.
                Assert.Single(result.Files, f => f.RelativePath == sharedSkillPath);
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isSuppressedSharedIdentity = suppressedSkillIdentities.Contains(skill.Name);
            string path = $".kilo/skills/{skill.Name}/SKILL.md";
            if (isSuppressedSharedIdentity)
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
            Assert.True(
                string.Equals(expectedDescription, RequireScalar(frontmatter, "description", skill.Name),
                    StringComparison.Ordinal),
                $"Skill '{skill.Name}' description mismatch.");
            Assert.True(
                string.Equals("MIT", RequireScalar(frontmatter, "license", skill.Name), StringComparison.Ordinal),
                $"Skill '{skill.Name}' license mismatch.");

            string expectedSkillBody = skill.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedSkillBody.EndsWith('\n')) expectedSkillBody += "\n";

            Assert.True(
                string.Equals(expectedSkillBody, skillBody, StringComparison.Ordinal),
                $"Skill '{skill.Name}' body mismatch.");
        }

        // Degradations: every agent whose capability profile configures permissions carries
        // 'permission-not-expressible', including Deny decisions to prevent silent capability
        // widening, and every agent that owns resources carries 'resource-links-rewritten'.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];
        string[] expectedDegraded = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                return capabilityVocabulary.Any(cap => prof.Permissions.ContainsKey(cap));
            })
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedDegraded);
        Assert.Equal(
            expectedDegraded,
            result.Degradations
                .Where(d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal))
                .Select(d => d.CanonicalIdentity)
                .OrderBy(n => n, StringComparer.Ordinal));

        // Discovery-guard degradations: every agent that owns resources records one
        // resource-links-rewritten degradation quoting the harness scanner glob.
        string[] expectedGuardDegraded = source.Agents
            .Where(a => a.Resources.Count > 0)
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedGuardDegraded);
        Assert.Equal(
            expectedGuardDegraded,
            result.Degradations
                .Where(d => string.Equals(d.Code, "resource-links-rewritten", StringComparison.Ordinal))
                .Select(d => d.CanonicalIdentity)
                .OrderBy(name => name, StringComparer.Ordinal));

        foreach (SquadDegradationRecord guardDegradation in result.Degradations
                     .Where(d => string.Equals(d.Code, "resource-links-rewritten", StringComparison.Ordinal)))
        {
            Assert.Equal("kilo", guardDegradation.Target);
            Assert.Equal(guardDegradation.CanonicalIdentity, guardDegradation.OutputIdentity);
            Assert.Equal(agentsByName[guardDegradation.CanonicalIdentity].BodyDigest,
                guardDegradation.InstructionDigest);
            Assert.Contains(".markdown", guardDegradation.Details, StringComparison.Ordinal);
            Assert.Contains("{agent,agents}/**/*.md", guardDegradation.Details, StringComparison.Ordinal);
        }

        // Verify canonical denies are recorded in degradation details (preventing capability widening)
        SquadDegradationRecord conductorDegradation = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == "conductor" &&
                 string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal));
        Assert.Contains("filesystem.search", conductorDegradation.Details, StringComparison.Ordinal);
        Assert.Contains("process.execute", conductorDegradation.Details, StringComparison.Ordinal);

        foreach (SquadDegradationRecord degradation in result.Degradations)
        {
            Assert.True(
                string.Equals("kilo", degradation.Target, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong target.");
            Assert.True(
                degradation.Code is "permission-not-expressible" or "resource-links-rewritten",
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong code.");
            Assert.True(
                string.Equals(degradation.CanonicalIdentity, degradation.OutputIdentity, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong output identity.");
            SquadAgent agent = agentsByName[degradation.CanonicalIdentity];
            Assert.True(
                string.Equals(agent.BodyDigest, degradation.InstructionDigest, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong instruction digest.");
        }
    }

    [Fact]
    public async Task RenderAsync_IsDeterministic()
    {
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Kilo],
            SquadDeploymentScope.Project);

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
            Assert.True(file1.Content.Span.SequenceEqual(file2.Content.Span),
                $"Content differed for file '{file1.RelativePath}'.");
        }
    }

    [Fact]
    public async Task RenderAsync_Kilo_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertDiscoveryGuardedProjectionAsync(
            new KiloRenderer(),
            SquadTarget.Kilo,
            $".kilo/agents/{ResourceBearingSquadFixture.AgentName}.md",
            ".kilo/agents",
            ".kilo/skills");
    }

    /// <summary>
    ///     The extension guard maps <c>references/x.md</c> to <c>references/x.markdown</c>; an agent
    ///     whose closure holds both spellings would overwrite one file with the other, so the
    ///     render fails closed instead of merging them.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Kilo_ExtensionGuardFailsClosedOnMdAndMarkdownAlias()
    {
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.Create();
        fixture.WriteAgentResource("references/x.md", "X markdown.\n");
        fixture.WriteAgentResource("references/x.markdown", "X markdown alias.\n");
        fixture.AppendAgentLink("[X md](bug-crusher-investigator/references/x.md)");
        fixture.AppendAgentLink("[X markdown](bug-crusher-investigator/references/x.markdown)");
        SquadRendererRegistry registry = new([new KiloRenderer()]);

        SquadRenderValidationException exception = await Assert.ThrowsAsync<SquadRenderValidationException>(() =>
            registry.RenderAsync(new SquadRenderRequest(
                fixture.ProductRoot,
                [SquadTarget.Kilo],
                SquadDeploymentScope.Project)));

        Assert.Contains(ResourceBearingSquadFixture.AgentName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("references/x.md", exception.Message, StringComparison.Ordinal);
        Assert.Contains("references/x.markdown", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An agent whose only resources are non-markdown is invisible to agent discovery as
    ///     projected, so the guard leaves its files and links byte-identical to the native layout.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Kilo_TextOnlyAgentResourcesProjectVerbatim()
    {
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.CreateWithTextOnlyAgentResources();
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderRequest request = new(
            fixture.ProductRoot,
            [SquadTarget.Kilo],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile payload = Assert.Single(
            result.Files,
            file => file.RelativePath ==
                    $".kilo/agents/{ResourceBearingSquadFixture.AgentName}/assets/agent-payload.txt");
        Assert.True(
            Encoding.UTF8.GetBytes(ResourceBearingSquadFixture.AgentPayload).AsSpan()
                .SequenceEqual(payload.Content.Span),
            "The text-only agent resource did not preserve its exact UTF-8 bytes.");

        SquadDeploymentFile principal = Assert.Single(
            result.Files,
            file => file.RelativePath == $".kilo/agents/{ResourceBearingSquadFixture.AgentName}.md");
        string body = Encoding.UTF8.GetString(principal.Content.Span);
        Assert.Contains(ResourceBearingSquadFixture.AgentTextOnlyLink, body, StringComparison.Ordinal);

        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        SquadAgent agent = Assert.Single(
            source.Agents,
            a => string.Equals(a.Name, ResourceBearingSquadFixture.AgentName, StringComparison.Ordinal));

        SquadDegradationRecord degradation = Assert.Single(
            result.Degradations,
            record => string.Equals(record.Code, "resource-links-rewritten", StringComparison.Ordinal) &&
                      string.Equals(record.CanonicalIdentity, ResourceBearingSquadFixture.AgentName,
                          StringComparison.Ordinal));
        Assert.Equal("kilo", degradation.Target);
        Assert.Equal(ResourceBearingSquadFixture.AgentName, degradation.OutputIdentity);
        Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
        Assert.False(
            string.IsNullOrWhiteSpace(degradation.Details),
            "The resource-links-rewritten degradation must explain the discovery guard.");
        Assert.Contains("project verbatim", degradation.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("projected with a '.markdown' extension", degradation.Details, StringComparison.Ordinal);
        Assert.Contains("{agent,agents}/**/*.md", degradation.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>.md</c> resource reached only transitively through a <c>.markdown</c> resource is
    /// renamed on disk, but the body never links to it, so the degradation must report the
    /// renamed file without claiming an authored link was rewritten.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Kilo_UnlinkedMarkdownResourceReportsRenameWithoutLinkRewrite()
    {
        using ResourceBearingSquadFixture fixture = ResourceBearingSquadFixture.CreateWithUnlinkedMarkdownResource();
        KiloRenderer renderer = new();
        SquadRenderRequest request = new(
            fixture.ProductRoot,
            [SquadTarget.Kilo],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await renderer.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        Assert.Contains(
            result.Files,
            file => file.RelativePath ==
                $".kilo/agents/{ResourceBearingSquadFixture.AgentName}/references/notes.markdown");

        SquadDegradationRecord degradation = Assert.Single(
            result.Degradations,
            record => string.Equals(record.Code, "resource-links-rewritten", StringComparison.Ordinal) &&
                      string.Equals(record.CanonicalIdentity, ResourceBearingSquadFixture.AgentName,
                          StringComparison.Ordinal));
        Assert.Contains("projected with a '.markdown' extension", degradation.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("authored links were rewritten", degradation.Details, StringComparison.Ordinal);
        Assert.Contains("no authored link required rewriting", degradation.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_Kilo_ResolvesConcreteModelsAndSuppressesInheritedModels()
    {
        using KiloModelResolutionSquadFixture fixture = KiloModelResolutionSquadFixture.Create();
        KiloRenderer renderer = new();
        SquadRenderRequest request = new(
            fixture.Path,
            [SquadTarget.Kilo],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await renderer.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // 1. Explicit kilo harness model (non-inherit) -> emits frontmatter model
        SquadDeploymentFile explicitFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".kilo/agents/{KiloModelResolutionSquadFixture.ExplicitModelAgent}.md");
        (YamlMappingNode explicitFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(explicitFile.Content.Span),
            KiloModelResolutionSquadFixture.ExplicitModelAgent);
        Assert.Equal(
            KiloModelResolutionSquadFixture.ExplicitKiloModel,
            RequireScalar(explicitFrontmatter, "model", KiloModelResolutionSquadFixture.ExplicitModelAgent));

        // 2. Explicit kilo harness model set to "inherit" -> omits frontmatter model
        SquadDeploymentFile inheritFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".kilo/agents/{KiloModelResolutionSquadFixture.InheritModelAgent}.md");
        (YamlMappingNode inheritFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(inheritFile.Content.Span),
            KiloModelResolutionSquadFixture.InheritModelAgent);
        Assert.False(
            inheritFrontmatter.Children.ContainsKey(new YamlScalarNode("model")),
            "Explicit 'inherit' kilo override must omit the 'model' key even when profile default is non-inherit.");

        // 3. Fallback to profile default when kilo harness is unspecified (non-inherit default) -> emits default model
        SquadDeploymentFile fallbackFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".kilo/agents/{KiloModelResolutionSquadFixture.FallbackModelAgent}.md");
        (YamlMappingNode fallbackFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(fallbackFile.Content.Span),
            KiloModelResolutionSquadFixture.FallbackModelAgent);
        Assert.Equal(
            KiloModelResolutionSquadFixture.FallbackDefaultModel,
            RequireScalar(fallbackFrontmatter, "model", KiloModelResolutionSquadFixture.FallbackModelAgent));

        // 4. Fallback to profile default when default is "inherit" and kilo harness is unspecified -> omits frontmatter model
        SquadDeploymentFile fallbackInheritFile = Assert.Single(
            result.Files,
            f => f.RelativePath == $".kilo/agents/{KiloModelResolutionSquadFixture.FallbackInheritAgent}.md");
        (YamlMappingNode fallbackInheritFrontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(fallbackInheritFile.Content.Span),
            KiloModelResolutionSquadFixture.FallbackInheritAgent);
        Assert.False(
            fallbackInheritFrontmatter.Children.ContainsKey(new YamlScalarNode("model")),
            "Profile default 'inherit' without kilo override must omit the 'model' key.");

        // 5. Verify degradation records preserve Deny permissions without capability widening
        Assert.Equal(4, result.Degradations.Count);
        Assert.All(result.Degradations, degradation =>
        {
            Assert.Equal("kilo", degradation.Target);
            Assert.Equal("permission-not-expressible", degradation.Code);
            Assert.Contains("filesystem.read", degradation.Details, StringComparison.Ordinal);
        });
    }

    /// <summary>
    ///     Every agent the source entitles to <c>kyber-weave</c> — the pure orchestrator included —
    ///     carries the server's wildcard entry in an emitted <c>permission</c> map, spelled with the
    ///     hyphen Kilo preserves.
    /// </summary>
    /// <remarks>
    ///     Kilo gates MCP tools through the same <c>permission</c> map as its built-in tools, under
    ///     the namespaced key <c>{server}_{tool}</c>, with the server name sanitized only for
    ///     characters outside <c>[a-zA-Z0-9_-]</c>. The hyphen in <c>kyber-weave</c> therefore
    ///     survives, and a <c>kyber_weave_*</c> spelling would parse cleanly and match no tool at
    ///     all — which is why the emitted bytes are pinned, not only the parsed key.
    ///     <para>
    ///     The expectation is computed from the loaded source through
    ///     <see cref="SquadMcpGrantProjection" /> and never read back from the renderer, so a
    ///     renderer that re-added the orchestrator exclusion fails here instead of agreeing with
    ///     itself. The map is an approval map, so a non-entitled agent is pinned to emitting no
    ///     entry rather than a <c>deny</c>: absence of <c>allow</c> is the withholding.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task RenderAsync_Kilo_GrantsKyberWeaveToEveryReadEntitledAgent()
    {
        const string KyberWeaveWildcard = "kyber-weave_*";
        const string UnderscoreSpelling = "kyber_weave_*";
        const string conductor = "conductor";

        SquadSource source = TestSquadCorpus.CanonicalSource;
        IReadOnlySet<string> entitled = SquadMcpGrantProjection.KyberWeaveEntitledAgents(source, SquadTarget.Kilo);
        Assert.Contains(conductor, entitled, StringComparer.Ordinal);

        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Kilo);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".kilo/agents/{agent.Name}.md");
            string text = Encoding.UTF8.GetString(file.Content.Span);
            (YamlMappingNode frontmatter, _) = SplitFrontmatter(text, agent.Name);

            if (!entitled.Contains(agent.Name))
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("permission")),
                    $"Agent '{agent.Name}' is not entitled to {SquadMcpGrantProjection.KyberWeaveServer} "
                    + "but renders a permission map, which this renderer emits only for an entitled agent.");
                continue;
            }

            YamlMappingNode permission = RequireMapping(frontmatter, "permission", agent.Name);
            Assert.Equal("allow", RequireScalar(permission, KyberWeaveWildcard, agent.Name));

            int frontmatterEnd = text.IndexOf("\n---\n", StringComparison.Ordinal);
            string frontmatterText = text[..(frontmatterEnd + 1)];
            Assert.Contains(KyberWeaveWildcard, frontmatterText, StringComparison.Ordinal);
            Assert.DoesNotContain(UnderscoreSpelling, frontmatterText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RenderAsync_ConcurrentRenders_ProduceConsistentOutputWithoutLocking()
    {
        KiloRenderer renderer = new();
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Kilo],
            SquadDeploymentScope.Project);

        Task<SquadRenderResult>[] tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => renderer.RenderAsync(request)))
            .ToArray();

        SquadRenderResult[] results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        SquadRenderResult first = results[0];
        foreach (SquadRenderResult other in results.Skip(1))
        {
            Assert.Equal(first.Files.Count, other.Files.Count);
            for (int i = 0; i < first.Files.Count; i++)
            {
                Assert.Equal(first.Files[i].RelativePath, other.Files[i].RelativePath);
                Assert.True(first.Files[i].Content.Span.SequenceEqual(other.Files[i].Content.Span));
            }
        }
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

    /// <summary>
    ///     Reads a nested mapping the way <see cref="RequireScalar" /> reads a scalar, naming the
    ///     keys that were present so a missing map is diagnosable from the failure alone.
    /// </summary>
    private static YamlMappingNode RequireMapping(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required mapping '{key}'. Present keys: {presentKeys}.");
        }

        return Assert.IsType<YamlMappingNode>(value);
    }

    /// <summary>
    ///     Minimal synthetic squad fixture exercising all branches of Kilo model resolution:
    ///     explicit non-inherit model, explicit inherit override, fallback to non-inherit default,
    ///     and fallback to inherit default.
    /// </summary>
    private sealed class KiloModelResolutionSquadFixture : IDisposable
    {
        internal const string ExplicitModelAgent = "explicit-model-agent";
        internal const string InheritModelAgent = "inherit-model-agent";
        internal const string FallbackModelAgent = "fallback-model-agent";
        internal const string FallbackInheritAgent = "fallback-inherit-agent";

        internal const string ExplicitKiloModel = "kilo-custom-v1";
        internal const string FallbackDefaultModel = "default-custom-v2";

        private readonly TempDirectory _tempDirectory = new();

        private KiloModelResolutionSquadFixture()
        {
        }

        internal string Path => _tempDirectory.Path;

        public void Dispose()
        {
            _tempDirectory.Dispose();
        }

        internal static KiloModelResolutionSquadFixture Create()
        {
            KiloModelResolutionSquadFixture fixture = new();
            fixture.Write("squad.yml", """
                                       schema: kyber-squad.squad/v1
                                       name: kilo-model-resolution-fixture
                                       version-source: kyber-weave-assembly
                                       default-bundle: full
                                       bundles:
                                         full: bundles/full.yml
                                       profiles:
                                         models: profiles/models.yml
                                         capabilities: profiles/capabilities.yml
                                         fallbacks: profiles/fallbacks.yml
                                       toolchain: toolchain.yml
                                       mcp: mcp.json
                                       """);
            fixture.Write("bundles/full.yml", $"""
                                               schema: kyber-squad.bundle/v1
                                               name: full
                                               agents:
                                                 - {ExplicitModelAgent}
                                                 - {InheritModelAgent}
                                                 - {FallbackModelAgent}
                                                 - {FallbackInheritAgent}
                                               skills: []
                                               """);
            fixture.Write("profiles/models.yml", $"""
                                                  schema: kyber-squad.model-profiles/v1
                                                  profiles:
                                                    explicit-profile:
                                                      default: inherit
                                                      kilo: {ExplicitKiloModel}
                                                    inherit-profile:
                                                      default: {FallbackDefaultModel}
                                                      kilo: inherit
                                                    fallback-profile:
                                                      default: {FallbackDefaultModel}
                                                    fallback-inherit-profile:
                                                      default: inherit
                                                  """);
            fixture.Write("profiles/capabilities.yml", """
                                                       schema: kyber-squad.capability-profiles/v1
                                                       capabilities:
                                                         - filesystem.read
                                                       profiles:
                                                         worker:
                                                           permissions:
                                                             filesystem.read: deny
                                                       """);
            fixture.Write("profiles/fallbacks.yml", """
                                                    schema: kyber-squad.fallback-profiles/v1
                                                    profiles:
                                                      role-skill:
                                                        no-primary-agent: skill
                                                        no-agent-primitive: skill
                                                        body-source: agent
                                                        output-identity:
                                                          unoccupied: agent-name
                                                          shared: reuse-skill
                                                          collision: role-prefixed-agent-name
                                                          prefix: role-
                                                        shared-identities: []
                                                    """);
            fixture.Write("toolchain.yml", """
                                           schema: kyber-squad.toolchain/v1
                                           required-features:
                                             - agent-ir/v1
                                           validated-release: null
                                           """);
            fixture.Write("mcp.json", """
                                      {
                                        "mcpServers": {}
                                      }
                                      """);

            WriteAgent(fixture, ExplicitModelAgent, "explicit-profile");
            WriteAgent(fixture, InheritModelAgent, "inherit-profile");
            WriteAgent(fixture, FallbackModelAgent, "fallback-profile");
            WriteAgent(fixture, FallbackInheritAgent, "fallback-inherit-profile");

            foreach (string schema in new[]
                     {
                         "squad",
                         "bundle",
                         "agent",
                         "model-profiles",
                         "capability-profiles"
                     })
                fixture.Write($"schemas/{schema}.schema.json", """
                                                               {
                                                                 "$schema": "https://json-schema.org/draft/2020-12/schema",
                                                                 "type": "object"
                                                               }
                                                               """);

            fixture.Write("schemas/fallback-profiles.schema.json", """
                                                                   {
                                                                     "$schema": "https://json-schema.org/draft/2020-12/schema",
                                                                     "$id": "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
                                                                     "type": "object"
                                                                   }
                                                                   """);

            return fixture;
        }

        private static void WriteAgent(KiloModelResolutionSquadFixture fixture, string name, string modelProfile)
        {
            fixture.Write($"agents/{name}.md", $"""
                                                ---
                                                schema: kyber-squad.agent/v1
                                                name: {name}
                                                description: Synthetic agent exercising model resolution for {name}.
                                                invocation: subagent
                                                model-profile: {modelProfile}
                                                capability-profile: worker
                                                copilot-tools: [vscode]
                                                delegates-to: []
                                                fallback: role-skill
                                                aliases: []
                                                ---
                                                Instruction body for {name}.
                                                """);
        }

        private void Write(string relativePath, string content)
        {
            string fullPath =
                System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            string? directory = System.IO.Path.GetDirectoryName(fullPath);
            if (directory is not null) Directory.CreateDirectory(directory);

            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }
    }
}
