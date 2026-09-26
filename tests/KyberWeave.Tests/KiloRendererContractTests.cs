using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="KiloRenderer"/> and validates the result
/// against Kilo's native agent and skill contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents lower to Kilo agents at <c>.kilo/agents/&lt;name&gt;.md</c>
/// and skills lower to <c>.kilo/skills/&lt;name&gt;/SKILL.md</c>. Verifies profile-declared
/// shared-identity suppression, model resolution from <c>models.yml</c>, deterministic output,
/// and structured degradation accounting (<c>permission-not-expressible</c> with matching
/// <c>InstructionDigest</c>).
/// </remarks>
public sealed class KiloRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Shared identities are read from the loaded fallback profile so the test follows the
    /// same generic single-projection contract as the renderer.
    /// </summary>
    private static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

    public void Dispose()
    {
        // No disposable state: suite reads canonical checked-in corpus.
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
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.Kilo],
            Scope: SquadDeploymentScope.Project);

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
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Kilo_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);
        SquadRendererRegistry registry = new([new KiloRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Kilo],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));
        int expectedSkillCount = source.Skills.Count - suppressedSkillCount;

        // C3: every rendered owner also projects its validated resource closure beside its
        // principal output, so the corpus count is principals plus emitted closures.
        int expectedFileCount =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + expectedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
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
                string.Equals(agent.Description, RequireScalar(frontmatter, "description", agent.Name), StringComparison.Ordinal),
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
                        string.Equals(expectedModel, RequireScalar(frontmatter, "model", agent.Name), StringComparison.Ordinal),
                        $"Agent '{agent.Name}' model mismatch: expected '{expectedModel}'.");
                }
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal))
            {
                concreteModelAgentCount++;
                Assert.True(
                    string.Equals(modelProfile.Default, RequireScalar(frontmatter, "model", agent.Name), StringComparison.Ordinal),
                    $"Agent '{agent.Name}' model mismatch: expected '{modelProfile.Default}'.");
            }
            else
            {
                inheritedModelAgentCount++;
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' should omit 'model' (profile default is inherit).");
            }

            // Exact comparison with normalized body
            string expectedAgentBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedAgentBody.EndsWith('\n'))
            {
                expectedAgentBody += "\n";
            }

            Assert.True(
                string.Equals(expectedAgentBody, body, StringComparison.Ordinal),
                $"Agent '{agent.Name}' body mismatch.");
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
            "glm5.3",
            RequireScalar(architectFrontmatter, "model", "architect"));

        // Shared identities suppression verification
        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".kilo/agents/{sharedIdentity}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".kilo/skills/{sharedIdentity}/SKILL.md");
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isSharedIdentity = sharedIdentities.Contains(skill.Name);
            string path = $".kilo/skills/{skill.Name}/SKILL.md";
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
            Assert.True(
                string.Equals(expectedDescription, RequireScalar(frontmatter, "description", skill.Name), StringComparison.Ordinal),
                $"Skill '{skill.Name}' description mismatch.");
            Assert.True(
                string.Equals("MIT", RequireScalar(frontmatter, "license", skill.Name), StringComparison.Ordinal),
                $"Skill '{skill.Name}' license mismatch.");

            string expectedSkillBody = skill.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedSkillBody.EndsWith('\n'))
            {
                expectedSkillBody += "\n";
            }

            Assert.True(
                string.Equals(expectedSkillBody, skillBody, StringComparison.Ordinal),
                $"Skill '{skill.Name}' body mismatch.");
        }

        // Degradations: all agents whose capability profile configures permissions carry 'permission-not-expressible',
        // including Deny decisions to prevent silent capability widening.
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
            result.Degradations.Select(d => d.CanonicalIdentity).OrderBy(n => n, StringComparer.Ordinal));

        // Verify canonical denies are recorded in degradation details (preventing capability widening)
        SquadDegradationRecord conductorDegradation = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == "conductor");
        Assert.Contains("filesystem.search", conductorDegradation.Details, StringComparison.Ordinal);
        Assert.Contains("process.execute", conductorDegradation.Details, StringComparison.Ordinal);

        foreach (SquadDegradationRecord degradation in result.Degradations)
        {
            Assert.True(
                string.Equals("kilo", degradation.Target, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong target.");
            Assert.True(
                string.Equals("permission-not-expressible", degradation.Code, StringComparison.Ordinal),
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
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Kilo],
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
    }

    [Fact]
    public async Task RenderAsync_Kilo_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new KiloRenderer(),
            SquadTarget.Kilo,
            $".kilo/agents/{ResourceBearingSquadFixture.AgentName}.md",
            ".kilo/agents",
            ".kilo/skills");
    }

    [Fact]
    public async Task RenderAsync_Kilo_ResolvesConcreteModelsAndSuppressesInheritedModels()
    {
        using KiloModelResolutionSquadFixture fixture = KiloModelResolutionSquadFixture.Create();
        KiloRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: fixture.Path,
            Targets: [SquadTarget.Kilo],
            Scope: SquadDeploymentScope.Project);

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

    [Fact]
    public async Task RenderAsync_ConcurrentRenders_ProduceConsistentOutputWithoutLocking()
    {
        KiloRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Kilo],
            Scope: SquadDeploymentScope.Project);

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
    /// Minimal synthetic squad fixture exercising all branches of Kilo model resolution:
    /// explicit non-inherit model, explicit inherit override, fallback to non-inherit default,
    /// and fallback to inherit default.
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
            {
                fixture.Write($"schemas/{schema}.schema.json", """
                    {
                      "$schema": "https://json-schema.org/draft/2020-12/schema",
                      "type": "object"
                    }
                    """);
            }
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

        public void Dispose() => _tempDirectory.Dispose();

        private void Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            string? directory = System.IO.Path.GetDirectoryName(fullPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
