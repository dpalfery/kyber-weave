using System.Text;
using KyberWeave.Tests.Fakes;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="CodexRenderer"/> and validates the result
/// against Codex's native agent TOML and skill contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents render as Codex native agent TOML primitives at
/// <c>.codex/agents/&lt;name&gt;.toml</c> and skills render at <c>.codex/skills/&lt;name&gt;/SKILL.md</c>.
/// Verifies profile-declared shared-identity suppression,
/// model resolution from <c>models.yml</c>, TOML structure and instruction encoding,
/// and structured degradation accounting (<c>permission-not-expressible</c> with matching <c>InstructionDigest</c>).
/// </remarks>
public sealed class CodexRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Shared identities are read from the loaded fallback profile so the test follows the
    /// same generic single-projection contract as the renderer.
    /// </summary>
    private static HashSet<string> SharedIdentities(SquadSource source)
    {
        return source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The shared identities whose canonical skill is suppressed: those carried by a subagent,
    /// whose file already holds the identical body. A primary shared identity reuses its
    /// canonical skill as the entry point, so it is emitted. Delegated to the renderer's own
    /// resolver so the test cannot drift from the contract.
    /// </summary>
    private static HashSet<string> SuppressedSkillIdentities(SquadSource source)
    {
        return SquadSharedIdentityProjection.SuppressedSkillIdentities(source, SharedIdentities(source));
    }

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void SupportedTargets_IsExactlyCodex()
    {
        SquadRendererRegistry registry = new([new CodexRenderer()]);

        Assert.Equal([SquadTarget.Codex], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new CodexRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Claude, SquadTarget.Codex],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonCodexTarget()
    {
        CodexRenderer renderer = new();
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Codex_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> sharedIdentities = SharedIdentities(source);
        HashSet<string> suppressedSkillIdentities = SuppressedSkillIdentities(source);
        SquadRendererRegistry registry = new([new CodexRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Codex],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        int suppressedSkillCount = source.Skills.Count(skill => suppressedSkillIdentities.Contains(skill.Name));
        int expectedSkillCount = source.Skills.Count - suppressedSkillCount;
        SquadAgent[] generatedPrimaryEntryPoints = source.Agents
            .Where(agent =>
                agent.Invocation == SquadInvocation.Primary &&
                string.Equals(
                    source.FallbackProfiles.Profiles[agent.Fallback].NoPrimaryAgent,
                    "skill",
                    StringComparison.Ordinal) &&
                !source.Skills.Any(skill => string.Equals(skill.Name, agent.Name, StringComparison.Ordinal)))
            .ToArray();
        int expectedPrimaryEntryPointArtifactCount =
            generatedPrimaryEntryPoints.Sum(agent => 1 + agent.Resources.Count);
        SquadAgent conductor = Assert.Single(generatedPrimaryEntryPoints, agent => agent.Name == "conductor");
        Assert.Equal(1 + conductor.Resources.Count, expectedPrimaryEntryPointArtifactCount);

        // C3: every rendered owner also projects its validated resource closure beside its
        // principal output. Primary agents with a skill fallback also project an entry-point
        // skill and its validated resource closure.
        int expectedFileCount = source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
                                                    + expectedSkillCount
                                                    + source.Skills.Where(skill =>
                                                            !suppressedSkillIdentities.Contains(skill.Name))
                                                        .Sum(skill => skill.Resources.Count)
                                                    + expectedPrimaryEntryPointArtifactCount;
        Assert.Equal(expectedFileCount, result.Files.Count);

        Assert.All(result.Files, f =>
        {
            Assert.Equal("codex", f.Target);
            Assert.True(
                f.RelativePath.StartsWith(".codex/agents/", StringComparison.Ordinal) ||
                f.RelativePath.StartsWith(".codex/skills/", StringComparison.Ordinal),
                $"File '{f.RelativePath}' does not start with expected Codex directory.");
        });

        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);

        // Agents verification
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".codex/agents/{agent.Name}.toml");

            string tomlContent = Encoding.UTF8.GetString(file.Content.Span);
            ParsedTomlAgent parsed = ParseCodexAgentToml(tomlContent, agent.Name);

            Assert.Equal(agent.Name, parsed.Name);
            Assert.Equal(agent.Description, parsed.Description);

            // Model resolution verification against loaded ModelProfiles
            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("codex", out string? expectedModel))
            {
                Assert.Equal(expectedModel, parsed.Model);
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal))
            {
                Assert.Equal(modelProfile.Default, parsed.Model);
            }
            else
            {
                Assert.Null(parsed.Model);
            }

            // Exact instructions body check
            string expectedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedBody.EndsWith('\n'))
            {
                expectedBody += "\n";
            }

            Assert.Equal(expectedBody, parsed.DeveloperInstructions);
        }

        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".codex/agents/{sharedIdentity}.toml");
            string sharedSkillPath = $".codex/skills/{sharedIdentity}/SKILL.md";
            if (suppressedSkillIdentities.Contains(sharedIdentity))
            {
                Assert.DoesNotContain(result.Files, f => f.RelativePath == sharedSkillPath);
            }
            else
            {
                // Primary shared identity: the canonical skill is the entry point and ships.
                Assert.Single(result.Files, f => f.RelativePath == sharedSkillPath);
            }
        }

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isSuppressedSharedIdentity = suppressedSkillIdentities.Contains(skill.Name);
            string path = $".codex/skills/{skill.Name}/SKILL.md";
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
            Assert.Equal(expectedDescription, RequireScalar(frontmatter, "description", skill.Name));
            Assert.Equal("MIT", RequireScalar(frontmatter, "license", skill.Name));

            string expectedSkillBody = skill.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedSkillBody.EndsWith('\n'))
            {
                expectedSkillBody += "\n";
            }

            Assert.Equal(expectedSkillBody, skillBody);
        }

        foreach (SquadAgent agent in generatedPrimaryEntryPoints)
        {
            SquadDeploymentFile entryPointSkill = Assert.Single(
                result.Files,
                file => file.RelativePath == $".codex/skills/{agent.Name}/SKILL.md");
            (YamlMappingNode frontmatter, string skillBody) = SplitFrontmatter(
                Encoding.UTF8.GetString(entryPointSkill.Content.Span),
                agent.Name);

            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            string expectedDescription = string.Join(" ", agent.Description.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            Assert.Equal(expectedDescription, RequireScalar(frontmatter, "description", agent.Name));
            Assert.Equal("MIT", RequireScalar(frontmatter, "license", agent.Name));

            string expectedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedBody.EndsWith('\n'))
            {
                expectedBody += "\n";
            }

            Assert.Equal(expectedBody, skillBody);

            foreach (SquadResource resource in agent.Resources)
            {
                SquadDeploymentFile resourceFile = Assert.Single(
                    result.Files,
                    file => file.RelativePath == $".codex/skills/{agent.Name}/{resource.RelativePath}");
                ReadOnlySpan<byte> expectedBytes = Encoding.UTF8.GetBytes(resource.Content);
                Assert.True(expectedBytes.SequenceEqual(resourceFile.Content.Span));
            }
        }

        // Degradations: every agent with non-deny capability profile must carry 'permission-not-expressible'
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];
        string[] expectedDegraded = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                return capabilityVocabulary.Any(cap =>
                    prof.Permissions.TryGetValue(cap, out SquadPermissionDecision decision) &&
                    decision != SquadPermissionDecision.Deny);
            })
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedDegraded);
        Assert.Equal(
            expectedDegraded,
            result.Degradations.Select(d => d.CanonicalIdentity).OrderBy(n => n, StringComparer.Ordinal));

        foreach (SquadDegradationRecord degradation in result.Degradations)
        {
            Assert.Equal("codex", degradation.Target);
            Assert.Equal("permission-not-expressible", degradation.Code);
            Assert.Equal(degradation.CanonicalIdentity, degradation.OutputIdentity);

            SquadAgent agent = agentsByName[degradation.CanonicalIdentity];
            Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
            Assert.NotNull(degradation.Details);
            Assert.Contains(agent.CapabilityProfile, degradation.Details, StringComparison.Ordinal);
        }

        // No permission widening detected
        Assert.DoesNotContain(
            result.Degradations,
            d => d.Code.Contains("widen", StringComparison.OrdinalIgnoreCase) ||
                 (d.Details is not null && d.Details.Contains("widening", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task RenderAsync_Codex_GlobalScopePlacesThePrimaryAgentEntryPointSkillUnderSkills()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Codex],
            SquadDeploymentScope.Global);

        SquadRenderResult result = await new CodexRenderer().RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        string expectedAgentBody = primaryAgent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!expectedAgentBody.EndsWith('\n'))
        {
            expectedAgentBody += "\n";
        }

        SquadDeploymentFile agentFile = Assert.Single(
            result.Files,
            file => file.RelativePath == $"agents/{primaryAgent.Name}.toml");
        ParsedTomlAgent parsedAgent = ParseCodexAgentToml(
            Encoding.UTF8.GetString(agentFile.Content.Span),
            primaryAgent.Name);
        Assert.Equal(primaryAgent.Name, parsedAgent.Name);
        Assert.Equal(expectedAgentBody, parsedAgent.DeveloperInstructions);

        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            file => file.RelativePath == $"skills/{primaryAgent.Name}/SKILL.md");
        (YamlMappingNode frontmatter, string skillBody) = SplitFrontmatter(
            Encoding.UTF8.GetString(skillFile.Content.Span),
            primaryAgent.Name);
        Assert.Equal(primaryAgent.Name, RequireScalar(frontmatter, "name", primaryAgent.Name));
        Assert.Equal("MIT", RequireScalar(frontmatter, "license", primaryAgent.Name));
        Assert.Equal(expectedAgentBody, skillBody);

        foreach (SquadResource resource in primaryAgent.Resources)
        {
            Assert.Single(
                result.Files,
                file => file.RelativePath == $"skills/{primaryAgent.Name}/{resource.RelativePath}");
        }

        Assert.DoesNotContain(
            result.Files,
            file => file.RelativePath.StartsWith(".codex/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_IsDeterministic()
    {
        SquadRendererRegistry registry = new([new CodexRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Codex],
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

        Assert.Equal(first.Degradations.Count, second.Degradations.Count);
        for (int i = 0; i < first.Degradations.Count; i++)
        {
            Assert.Equal(first.Degradations[i], second.Degradations[i]);
        }
    }

    [Fact]
    public async Task RenderAsync_Codex_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new CodexRenderer(),
            SquadTarget.Codex,
            ".codex/agents/bug-crusher-investigator.toml",
            ".codex/agents",
            ".codex/skills");
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

        SquadRenderResult result = await new CodexRenderer().RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [SquadTarget.Codex],
            SquadDeploymentScope.Project));

        SquadDeploymentFile rendered = Assert.Single(
            result.Files,
            f => f.RelativePath == $".codex/skills/{probe}/SKILL.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(rendered.Content.Span),
            probe);

        string[] keys = frontmatter.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["name", "description", "license"], keys);
    }

    /// <summary>
    /// Bodies with backslashes and triple quotes must round-trip through rendered TOML:
    /// EscapeTomlMultiline escapes backslashes first, and an independent parser (not a
    /// reverse of the renderer) recovers the original body.
    /// </summary>
    [Fact]
    public async Task RenderAgent_EscapesBackslashesAndTripleQuotes_RoundTripsThroughTomlParse()
    {
        const string fixtureBody =
            "Path C:\\Users\\agent\\project and a fence:\n" +
            "\"\"\"quoted block\"\"\"\n" +
            "trailing slash \\\n";

        string sourceRoot = WriteMinimalSquadSource(_temp.Path, fixtureBody);

        SquadRenderResult result = await new CodexRenderer().RenderAsync(new SquadRenderRequest(
            sourceRoot,
            [SquadTarget.Codex],
            SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile agentFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".codex/agents/escape-probe.toml");

        string toml = Encoding.UTF8.GetString(agentFile.Content.Span);
        ParsedTomlAgent parsed = ParseCodexAgentToml(toml, "escape-probe");

        string expectedBody = fixtureBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!expectedBody.EndsWith('\n'))
        {
            expectedBody += "\n";
        }

        Assert.Equal(expectedBody, parsed.DeveloperInstructions);
        Assert.Contains("developer_instructions = \"\"\"", toml, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*instructions\s*=", toml);
    }

    private sealed record ParsedTomlAgent(
        string Name,
        string Description,
        string? Model,
        string DeveloperInstructions);

    /// <summary>
    /// Strict minimal TOML reader for Codex agent files: basic strings plus one multiline
    /// <c>developer_instructions</c> field. Escape handling walks the input so <c>\\</c>
    /// and <c>\"""</c> decode correctly without mirroring renderer replace order.
    /// </summary>
    private static ParsedTomlAgent ParseCodexAgentToml(string toml, string identity)
    {
        const string multilineOpen = "developer_instructions = \"\"\"\n";
        int multilineIdx = toml.IndexOf(multilineOpen, StringComparison.Ordinal);
        Assert.True(
            multilineIdx >= 0,
            $"Agent '{identity}' is missing required field 'developer_instructions' as a multiline string.");

        string header = toml[..multilineIdx];
        string afterOpen = toml[(multilineIdx + multilineOpen.Length)..];
        int closeIdx = FindMultilineClose(afterOpen);
        Assert.True(closeIdx >= 0, $"Agent '{identity}' developer_instructions block is missing closing quotes.");

        string developerInstructions = UnescapeTomlStringContent(afterOpen[..closeIdx]);

        string? name = null;
        string? description = null;
        string? model = null;

        foreach (string line in header.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            Match match = Regex.Match(trimmed, @"^(?<key>[A-Za-z0-9_\-]+)\s*=\s*""(?<val>.*)""$");
            Assert.True(match.Success, $"Agent '{identity}' has an unparseable header line: '{trimmed}'.");

            string key = match.Groups["key"].Value;
            string val = UnescapeTomlStringContent(match.Groups["val"].Value);

            switch (key)
            {
                case "name":
                    name = val;
                    break;
                case "description":
                    description = val;
                    break;
                case "model":
                    model = val;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Agent '{identity}' header contains unexpected key '{key}'.");
            }
        }

        Assert.NotNull(name);
        Assert.NotNull(description);

        return new ParsedTomlAgent(name, description, model, developerInstructions);
    }

    /// <summary>
    /// Locates the closing <c>"""</c> that is not part of a <c>\"""</c> escape sequence.
    /// </summary>
    private static int FindMultilineClose(string afterOpen)
    {
        for (int i = 0; i <= afterOpen.Length - 3; i++)
        {
            if (afterOpen[i] != '"' || afterOpen[i + 1] != '"' || afterOpen[i + 2] != '"')
            {
                continue;
            }

            int backslashCount = 0;
            for (int j = i - 1; j >= 0 && afterOpen[j] == '\\'; j--)
            {
                backslashCount++;
            }

            // An odd number of preceding backslashes means the first quote is escaped.
            if (backslashCount % 2 == 1)
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// Decodes TOML basic/multiline escape sequences by scanning rather than ordered replaces.
    /// </summary>
    private static string UnescapeTomlStringContent(string value)
    {
        StringBuilder builder = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            Assert.True(i + 1 < value.Length, "TOML string ends with a dangling backslash escape.");
            char next = value[++i];
            builder.Append(next switch
            {
                '\\' => '\\',
                '"' => '"',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => throw new InvalidOperationException($"Unsupported TOML escape '\\{next}'.")
            });
        }

        return builder.ToString();
    }

    private static string WriteMinimalSquadSource(string root, string agentBody)
    {
        Write(root, "squad.yml", """
                                 schema: kyber-squad.squad/v1
                                 name: kyber-squad
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
        Write(root, "bundles/full.yml", """
                                        schema: kyber-squad.bundle/v1
                                        name: full
                                        agents:
                                          - escape-probe
                                        skills: []
                                        """);
        Write(root, "profiles/models.yml", """
                                           schema: kyber-squad.model-profiles/v1
                                           profiles:
                                             general:
                                               default: inherit
                                           """);
        Write(root, "profiles/capabilities.yml", """
                                                 schema: kyber-squad.capability-profiles/v1
                                                 capabilities:
                                                   - filesystem.read
                                                 profiles:
                                                   worker:
                                                     permissions:
                                                       filesystem.read: deny
                                                 """);
        Write(root, "profiles/fallbacks.yml", """
                                              schema: kyber-squad.fallback-profiles/v1
                                              profiles:
                                                role-skill:
                                                  no-primary-agent: skill
                                                  no-agent-primitive: skill
                                              """);
        Write(root, "toolchain.yml", """
                                     schema: kyber-squad.toolchain/v1
                                     required-features:
                                       - agent-ir/v1
                                     validated-release: null
                                     """);
        Write(root, "mcp.json", """
                                {
                                  "mcpServers": {
                                    "kyber-weave": {
                                      "command": "kyber-weave-mcp",
                                      "args": ["--repo-root", "."]
                                    }
                                  }
                                }
                                """);
        Write(root, "agents/escape-probe.md",
            "---\n" +
            "schema: kyber-squad.agent/v1\n" +
            "name: escape-probe\n" +
            "description: Probe agent for TOML escape round-trip.\n" +
            "invocation: subagent\n" +
            "model-profile: general\n" +
            "capability-profile: worker\n" +
            "copilot-tools: [vscode]\n" +
            "delegates-to: []\n" +
            "fallback: role-skill\n" +
            "aliases: []\n" +
            "---\n" +
            agentBody);
        Write(root, "schemas/squad.schema.json", MinimalSchema);
        Write(root, "schemas/bundle.schema.json", MinimalSchema);
        Write(root, "schemas/agent.schema.json", MinimalSchema);
        Write(root, "schemas/model-profiles.schema.json", MinimalSchema);
        Write(root, "schemas/capability-profiles.schema.json", MinimalSchema);
        Write(root, "schemas/fallback-profiles.schema.json", """
                                                             {
                                                               "$schema": "https://json-schema.org/draft/2020-12/schema",
                                                               "$id": "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
                                                               "type": "object",
                                                               "additionalProperties": false,
                                                               "required": ["schema", "profiles"],
                                                               "properties": {
                                                                 "schema": { "const": "kyber-squad.fallback-profiles/v1" },
                                                                 "profiles": {
                                                                   "type": "object",
                                                                   "additionalProperties": {
                                                                     "type": "object",
                                                                     "additionalProperties": false,
                                                                     "required": ["no-primary-agent", "no-agent-primitive"],
                                                                     "properties": {
                                                                       "no-primary-agent": { "enum": ["skill", "omit"] },
                                                                       "no-agent-primitive": { "enum": ["skill", "omit"] },
                                                                       "body-source": { "const": "agent" },
                                                                       "output-identity": {
                                                                         "type": "object",
                                                                         "additionalProperties": false,
                                                                         "required": ["unoccupied", "shared", "collision", "prefix"],
                                                                         "properties": {
                                                                           "unoccupied": { "const": "agent-name" },
                                                                           "shared": { "const": "reuse-skill" },
                                                                           "collision": { "const": "role-prefixed-agent-name" },
                                                                           "prefix": { "const": "role-" }
                                                                         }
                                                                       },
                                                                       "shared-identities": {
                                                                         "type": "array",
                                                                         "uniqueItems": true,
                                                                         "items": { "type": "string", "minLength": 1 }
                                                                       }
                                                                     }
                                                                   }
                                                                 }
                                                               }
                                                             }
                                                             """);

        return root;
    }

    private const string MinimalSchema = """
                                         {
                                           "$schema": "https://json-schema.org/draft/2020-12/schema",
                                           "type": "object"
                                         }
                                         """;

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
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
}
