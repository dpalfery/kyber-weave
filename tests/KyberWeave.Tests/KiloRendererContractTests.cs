using System.Text;
using KyberWeave.Tests.Fakes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;
using static KyberWeave.Tests.Fakes.SquadRenderedOutput;

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
    ///     Kilo's own decisions on the real corpus: every agent is a native agent file carrying its
    ///     invocation as <c>mode</c>; every resource-owning agent records the discovery guard with
    ///     Kilo's scanner glob; and every configured capability decision, denies included, is
    ///     recorded as unexpressed, because Kilo's permission map is keyed by tool name. The skill
    ///     document and record identity are the shared pipeline's, checked in
    ///     <see cref="Squad.SquadRendererSharedContractTests"/>.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Kilo_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await new SquadRendererRegistry([new KiloRenderer()]).RenderAsync(
            new SquadRenderRequest(ProductRoot, [SquadTarget.Kilo], SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == $".kilo/agents/{agent.Name}.md");
            (YamlMappingNode frontmatter, _) = SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span), agent.Name);
            Assert.Equal(agent.Description, RequireScalar(frontmatter, "description", agent.Name));
            Assert.Equal(
                agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent",
                RequireScalar(frontmatter, "mode", agent.Name));
        }

        Assert.Equal(
            source.Agents.Where(agent => agent.Resources.Count > 0).Select(agent => agent.Name).Order(StringComparer.Ordinal),
            result.Degradations.Where(d => d.Code == "resource-links-rewritten").Select(d => d.CanonicalIdentity).Order(StringComparer.Ordinal));
        Assert.All(
            result.Degradations.Where(d => d.Code == "resource-links-rewritten"),
            d => Assert.Contains("{agent,agents}/**/*.md", d.Details, StringComparison.Ordinal));

        string[] constrained = source.Agents
            .Where(agent => source.CapabilityProfiles.Capabilities.Any(
                source.CapabilityProfiles.Profiles[agent.CapabilityProfile].Permissions.ContainsKey))
            .Select(agent => agent.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(constrained);
        Assert.Equal(
            constrained,
            result.Degradations.Where(d => d.Code == "permission-not-expressible").Select(d => d.CanonicalIdentity).Order(StringComparer.Ordinal));
        Assert.All(result.Degradations, d => Assert.True(
            d.Code is "permission-not-expressible" or "resource-links-rewritten",
            $"Unexpected Kilo degradation code '{d.Code}' for '{d.CanonicalIdentity}'."));

        // Denies are recorded too: silently dropping one would widen the deployed agent.
        SquadDegradationRecord conductor = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == "conductor" && d.Code == "permission-not-expressible");
        Assert.Contains("filesystem.search", conductor.Details, StringComparison.Ordinal);
        Assert.Contains("process.execute", conductor.Details, StringComparison.Ordinal);
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
    ///     Every agent the source entitles to <c>kw-docs</c> — the pure orchestrator included —
    ///     carries the server's wildcard entry in an emitted <c>permission</c> map, spelled with the
    ///     hyphen Kilo preserves.
    /// </summary>
    /// <remarks>
    ///     Kilo gates MCP tools through the same <c>permission</c> map as its built-in tools, under
    ///     the namespaced key <c>{server}_{tool}</c>, with the server name sanitized only for
    ///     characters outside <c>[a-zA-Z0-9_-]</c>. The hyphen in <c>kw-docs</c> therefore
    ///     survives, and a <c>kw_docs_*</c> spelling would parse cleanly and match no tool at
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
        const string KyberWeaveWildcard = "kw-docs_*";
        const string UnderscoreSpelling = "kw_docs_*";
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
