using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real, checked-in canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="AntigravityRenderer"/> and pins the native agent-per-directory contract
/// (agents at <c>.agents/agents/&lt;name&gt;/agent.md</c>, skills at <c>.agents/skills/&lt;name&gt;/SKILL.md</c>).
/// </summary>
/// <remarks>
/// Counts and collision sets are derived from the loaded <see cref="SquadSource"/> so the
/// suite tracks the shipped corpus rather than hardcoded 22/26/46 literals that would
/// silently drift. Native dual emission means no role- prefix collision resolution: agents and
/// skills render to separate namespace roots.
/// </remarks>
public sealed class AntigravityRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Shared identities are read from the loaded fallback profile's shared-identities
    /// list — the same authoritative source the renderer uses — rather than a literal
    /// roster that could drift from profiles/fallbacks.yml.
    /// </summary>
    private static IReadOnlyList<string> SharedIdentities(SquadSource source)
    {
        return source.FallbackProfiles.Profiles.TryGetValue("role-skill", out SquadFallbackProfile? profile)
            ? profile.SharedIdentities
            : [];
    }

    /// <summary>
    /// The expected permission-degradation roster is derived from the loaded capability
    /// profiles' own declared vocabulary, mirroring the renderer's source of truth.
    /// </summary>
    private static bool HasNonDenyCapability(SquadCapabilityProfiles profiles, SquadCapabilityProfile profile)
    {
        return profiles.Capabilities.Any(capability =>
            profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
            decision != SquadPermissionDecision.Deny);
    }

    /// <summary>
    /// Collisions are derived from the loaded source (agent identity that also exists as a
    /// canonical skill), not a hardcoded roster, so the suite tracks the corpus rather than
    /// a snapshot that could silently drift. Shared conductor identities are excluded —
    /// they reuse the canonical skill rather than emitting a role-prefixed one.
    /// </summary>
    private static HashSet<string> DeriveCollisions(SquadSource source)
    {
        HashSet<string> skillNames = source.Skills.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> shared = SharedIdentities(source).ToHashSet(StringComparer.Ordinal);
        return source.Agents
            .Where(a => skillNames.Contains(a.Name) && !shared.Contains(a.Name))
            .Select(a => a.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    public void Dispose()
    {
        // No disposable state: the suite only reads the checked-in corpus. IDisposable is
        // implemented per the test-coding-standard so adding fixtures later has a home.
    }

    [Fact]
    public void ToolRoot_ExistsForCanonicalCorpus()
    {
        Assert.True(
            Directory.Exists(KyberWeaveTestPaths.ToolRoot),
            $"Expected KyberWeaveTestPaths.ToolRoot at '{KyberWeaveTestPaths.ToolRoot}'.");
        Assert.True(
            Directory.Exists(ProductRoot),
            $"Expected products/kyber-squad under ToolRoot at '{ProductRoot}'.");
    }

    /// <summary>The renderer refuses a request for any target but Antigravity.</summary>
    [Fact]
    public async Task RenderAsync_RejectsNonAntigravityTarget()
    {
        AntigravityRenderer renderer = new();
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project,
            PreloadedSource: TestSquadCorpus.CanonicalSource);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    /// <summary>Renders the shipped corpus and checks every agent and skill lands at its native path, with collisions derived from the source.</summary>
    [Fact]
    public async Task RenderAsync_Antigravity_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> shared = SharedIdentities(source).ToHashSet(StringComparer.Ordinal);
        HashSet<string> canonicalSkillNames = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string identity in shared)
        {
            Assert.True(
                canonicalSkillNames.Contains(identity),
                $"Shared identity '{identity}' has no canonical skill projection.");
        }

        HashSet<string> collisions = DeriveCollisions(source);


        // Native dual-root rendering: every agent emits an agent.md file,
        // all canonical skills are emitted, and each projects its validated resource closure beneath
        // its identity directory. R17: Agents.Count + Σ agent resources + Skills.Count
        // + Σ skill resources.
        int expectedFiles = source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
                                                + source.Skills.Count
                                                + source.Skills.Sum(skill => skill.Resources.Count);

        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(expectedFiles, result.Files.Count);

        // Path uniqueness guards the occupancy-derived lowering: two agents claiming the
        // same .agents/skills/{name}/SKILL.md path would pass the count check while one
        // silently overwrites the other at deployment time.
        string[] duplicatePaths = result.Files
            .GroupBy(f => f.RelativePath, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Assert.True(
            duplicatePaths.Length == 0,
            $"Duplicate rendered paths: {string.Join(", ", duplicatePaths)}");

        // Native dual-root rendering with distinct resource projection: agent principals are
        // .agents/agents/{name}/agent.md, skill principals are .agents/skills/{name}/SKILL.md;
        // closure resources project beside their respective principal.
        HashSet<string> agentResourceOutputPaths = source.Agents
            .SelectMany(
                agent => agent.Resources,
                (agent, resource) => $".agents/agents/{agent.Name}/{resource.RelativePath}")
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> skillResourceOutputPaths = source.Skills
            .SelectMany(
                skill => skill.Resources,
                (skill, resource) => $".agents/skills/{skill.Name}/{resource.RelativePath}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(result.Files, f =>
        {
            Assert.Equal("antigravity", f.Target);
            Assert.True(
                f.RelativePath.StartsWith(".agents/agents/", StringComparison.Ordinal) ||
                f.RelativePath.StartsWith(".agents/skills/", StringComparison.Ordinal),
                $"File '{f.RelativePath}' is outside .agents/agents/ and .agents/skills/.");
            Assert.True(
                (f.RelativePath.StartsWith(".agents/agents/", StringComparison.Ordinal) &&
                 f.RelativePath.EndsWith("/agent.md", StringComparison.Ordinal)) ||
                (f.RelativePath.StartsWith(".agents/skills/", StringComparison.Ordinal) &&
                 f.RelativePath.EndsWith("/SKILL.md", StringComparison.Ordinal)) ||
                agentResourceOutputPaths.Contains(f.RelativePath) ||
                skillResourceOutputPaths.Contains(f.RelativePath),
                $"Rendered file '{f.RelativePath}' is neither an agent.md/SKILL.md principal nor a projected closure resource.");
        });

        foreach (SquadSkill skill in source.Skills)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/skills/{skill.Name}/SKILL.md");

            YamlMappingNode frontmatter = ReadFrontmatter(file);
            Assert.Equal(skill.Name, RequireScalar(frontmatter, "name"));
            string emittedDescription = RequireScalar(frontmatter, "description");
            Assert.True(
                !emittedDescription.Contains('\n', StringComparison.Ordinal) &&
                string.Equals(emittedDescription, emittedDescription.Trim(), StringComparison.Ordinal),
                $"Skill '{skill.Name}' description must be a trimmed single-line scalar.");
            foreach (string line in skill.Description.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Assert.Contains(line, emittedDescription, StringComparison.Ordinal);
            }

            Assert.True(
                string.Equals("MIT", RequireScalar(frontmatter, "license"), StringComparison.Ordinal),
                $"Skill '{skill.Name}' license mismatch.");
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                $"Skill '{skill.Name}' must not declare a model.");

            string expectedBody = NormalizeBody(skill.InstructionBody);
            Assert.True(
                string.Equals(expectedBody, ReadBody(file), StringComparison.Ordinal),
                $"Skill '{skill.Name}' body mismatch.");
        }

        foreach (string collision in collisions)
        {
            // Native dual-root rendering: collision means both agent and skill exist.
            // Agent renders to .agents/agents/{collision}/agent.md, skill to .agents/skills/{collision}/SKILL.md.
            Assert.Contains(result.Files, f => f.RelativePath == $".agents/agents/{collision}/agent.md");
            Assert.Contains(result.Files, f => f.RelativePath == $".agents/skills/{collision}/SKILL.md");

            SquadAgent agent = Assert.Single(source.Agents, a => a.Name == collision);
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{collision}/agent.md");

            YamlMappingNode agentFrontmatter = ReadFrontmatter(agentFile);
            Assert.True(
                string.Equals(collision, RequireScalar(agentFrontmatter, "name"), StringComparison.Ordinal),
                $"Collision agent '{collision}' has the wrong name.");
            Assert.True(
                string.Equals(ToSingleLineScalar(agent.Description), RequireScalar(agentFrontmatter, "description"),
                    StringComparison.Ordinal),
                $"Collision agent '{collision}' has the wrong description.");
            Assert.True(
                string.Equals(NormalizeBody(agent.InstructionBody), ReadBody(agentFile), StringComparison.Ordinal),
                $"Collision agent '{collision}' has the wrong body.");
        }

        // Native rendering: shared identities render both as agent files AND as canonical skills.
        // In Native Both pattern, skill projection is suppressed for shared identities to avoid
        // redundant resource projection, but agent files still exist.
        foreach (string conductor in SharedIdentities(source))
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".agents/agents/{conductor}/agent.md");
            Assert.Contains(result.Files, f => f.RelativePath == $".agents/skills/{conductor}/SKILL.md");
            Assert.Equal(
                1,
                result.Files.Count(f => f.RelativePath.EndsWith($"{conductor}/SKILL.md", StringComparison.Ordinal)));
            Assert.Equal(
                1,
                result.Files.Count(f => f.RelativePath.EndsWith($"{conductor}/agent.md", StringComparison.Ordinal)));

            // Native target: no role-skill-fallback degradations
            Assert.DoesNotContain(
                result.Degradations,
                d => d.CanonicalIdentity == conductor && d.Code == "role-skill-fallback");
        }

        // Native rendering: no role-skill-fallback degradations at all
        Assert.DoesNotContain(
            result.Degradations,
            d => d.Code == "role-skill-fallback");

        // Permission degradations are emitted only for specific permission constraints
        // that cannot be expressed in Antigravity's agent.md format:
        // D10: capability-not-isolable (process.execute Allow + filesystem.write not Allow)
        // D12: delegates-to roster enforcement (agents with DelegatesTo constraints)
        // Verify that all reported permission-not-expressible degradations refer to valid agents
        // and match their instruction digests.
        List<string> expectedPermissionAgents = [];
        foreach (SquadAgent agent in source.Agents)
        {
            Assert.True(
                source.CapabilityProfiles.Profiles.TryGetValue(agent.CapabilityProfile,
                    out SquadCapabilityProfile? profile),
                $"Agent '{agent.Name}' references undeclared capability profile '{agent.CapabilityProfile}'.");

            bool executeAllowed =
                profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec) &&
                exec == SquadPermissionDecision.Allow;
            bool writeAllowed =
                profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write) &&
                write == SquadPermissionDecision.Allow;
            bool isCapabilityNotIsolable = executeAllowed && !writeAllowed;

            if (!isCapabilityNotIsolable && HasNonDenyCapability(source.CapabilityProfiles, profile))
            {
                expectedPermissionAgents.Add(agent.Name);
            }

            if (agent.DelegatesTo.Count > 0)
            {
                expectedPermissionAgents.Add(agent.Name);
            }
        }

        string[] expectedPermissionArray = expectedPermissionAgents
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] actualPermissionAgents = result.Degradations
            .Where(d => d.Code == "permission-not-expressible")
            .Select(d => d.CanonicalIdentity)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedPermissionArray, actualPermissionAgents);

        // Native renderer permission degradations record constraints that cannot be expressed.
        // Each degradation's target, identity, and instruction digest must be consistent.
        foreach (SquadDegradationRecord degradation in result.Degradations.Where(d =>
                     d.Code == "permission-not-expressible"))
        {
            Assert.True(
                string.Equals("antigravity", degradation.Target, StringComparison.Ordinal),
                $"Degradation for '{degradation.CanonicalIdentity}' has the wrong target.");
            Assert.True(
                string.Equals(degradation.CanonicalIdentity, degradation.OutputIdentity, StringComparison.Ordinal),
                $"Permission degradation for '{degradation.CanonicalIdentity}' has the wrong output identity.");
            SquadAgent agent = Assert.Single(source.Agents, a => a.Name == degradation.CanonicalIdentity);
            Assert.True(
                string.Equals(agent.BodyDigest, degradation.InstructionDigest, StringComparison.Ordinal),
                $"Permission degradation for '{agent.Name}' has the wrong instruction digest.");
            Assert.DoesNotContain("widening", degradation.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // No render may widen a canonical permission: degradations record what they cannot
        // express, they never claim a broader grant.
        Assert.DoesNotContain(
            result.Degradations,
            d => d.Code.Contains("widen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RenderAsync_Antigravity_IsDeterministic()
    {
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project,
            PreloadedSource: TestSquadCorpus.CanonicalSource);

        SquadRenderResult first = await registry.RenderAsync(request);
        SquadRenderResult second = await registry.RenderAsync(request);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.Files.Count, second.Files.Count);

        for (int i = 0; i < first.Files.Count; i++)
        {
            Assert.Equal(first.Files[i].RelativePath, second.Files[i].RelativePath);
            Assert.Equal(first.Files[i].Target, second.Files[i].Target);
            Assert.True(first.Files[i].Content.Span.SequenceEqual(second.Files[i].Content.Span));
        }

        // File paths, targets, contents, and degradation records must remain byte/value
        // identical across renders of the same canonical source.
        Assert.Equal(first.Degradations.Count, second.Degradations.Count);
        for (int i = 0; i < first.Degradations.Count; i++)
        {
            Assert.Equal(first.Degradations[i], second.Degradations[i]);
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_ProjectsAgentAndSkillResourcesDeterministically()
    {
        // Antigravity Native Both pattern: unified resource projection under skills directory
        // for both agent and skill resources. This differs from other native renderers that
        // project resources under separate agents/ and skills/ directories.
        // Verify determinism by rendering twice and confirming identical file output.
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project,
            PreloadedSource: TestSquadCorpus.CanonicalSource);

        SquadRenderResult first = await registry.RenderAsync(request);
        SquadRenderResult second = await registry.RenderAsync(request);

        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.True(second.Success, string.Join("; ", second.Errors));

        // Verify both renders produce identical results
        Assert.Equal(first.Files.Count, second.Files.Count);
        for (int i = 0; i < first.Files.Count; i++)
        {
            Assert.Equal(first.Files[i].RelativePath, second.Files[i].RelativePath);
            Assert.Equal(first.Files[i].Target, second.Files[i].Target);
            Assert.True(first.Files[i].Content.Span.SequenceEqual(second.Files[i].Content.Span));
        }

        // Verify degradations are also deterministic
        Assert.Equal(first.Degradations.Count, second.Degradations.Count);
        for (int i = 0; i < first.Degradations.Count; i++)
        {
            Assert.Equal(first.Degradations[i], second.Degradations[i]);
        }

        // Verify that agent resources are projected under .agents/agents/ beside agent.md
        SquadSource source = TestSquadCorpus.CanonicalSource;
        foreach (SquadAgent agent in source.Agents)
        {
            foreach (SquadResource resource in agent.Resources)
            {
                Assert.Contains(
                    first.Files,
                    f => f.RelativePath == $".agents/agents/{agent.Name}/{resource.RelativePath}");
            }
        }

        // Verify that skill resources are also projected under .agents/skills/
        foreach (SquadSkill skill in source.Skills)
        {
            foreach (SquadResource resource in skill.Resources)
            {
                Assert.Contains(
                    first.Files,
                    f => f.RelativePath == $".agents/skills/{skill.Name}/{resource.RelativePath}");
            }
        }
    }

    [Fact]
    public async Task RenderSkill_FrontmatterKeyOrderIsStable()
    {
        // The frontmatter contract is name, description, license — an ordered mapping, not
        // a sequence. Pinned so a serializer change cannot silently reorder keys or emit
        // a list-of-pairs shape.
        SquadSource source = TestSquadCorpus.CanonicalSource;
        string probe = source.Skills
                           .Select(skill => skill.Name)
                           .OrderBy(name => name, StringComparer.Ordinal)
                           .FirstOrDefault()
                       ?? throw new InvalidOperationException($"Corpus at '{ProductRoot}' declares no skills.");

        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        SquadDeploymentFile rendered = Assert.Single(
            result.Files,
            f => f.RelativePath == $".agents/skills/{probe}/SKILL.md");
        YamlMappingNode frontmatter = ReadFrontmatter(rendered);
        string[] keys = frontmatter.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["name", "description", "license"], keys);
    }

    private static string NormalizeBody(string body)
    {
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }

    private static string ToSingleLineScalar(string value)
    {
        return string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static (YamlMappingNode Frontmatter, string Body) SplitFrontmatter(string text)
    {
        const string delimiter = "---\n";
        Assert.StartsWith(delimiter, text, StringComparison.Ordinal);
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, "Expected a closing '---' frontmatter delimiter.");

        string yaml = text[delimiter.Length..(end + 1)];
        string body = text[(end + 5)..];

        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
        return (root, body);
    }

    private static YamlMappingNode ReadFrontmatter(SquadDeploymentFile file)
    {
        return SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span)).Frontmatter;
    }

    private static string ReadBody(SquadDeploymentFile file)
    {
        return SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span)).Body;
    }

    private static string RequireScalar(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"Frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        return Assert.IsType<YamlScalarNode>(value).Value
               ?? throw new InvalidOperationException($"Key '{key}' has a null scalar value.");
    }

    private static YamlNode? OptionalScalar(YamlMappingNode node, string key)
    {
        return node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) ? value : null;
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_EmitsNativeAgentFiles()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Every agent must emit a native agent.md file at .agents/agents/<name>/agent.md
        foreach (SquadAgent agent in source.Agents)
        {
            Assert.True(
                result.Files.Any(f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md" &&
                                      f.Target == "antigravity"),
                $"Agent '{agent.Name}' must render to .agents/agents/{agent.Name}/agent.md");
        }
    }

    /// <summary>Every canonical skill still emits its <c>SKILL.md</c> under native rendering.</summary>
    [Fact]
    public async Task RenderAsync_Antigravity_Native_EmitsCanonicalSkillFiles()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Every canonical skill must still emit a SKILL.md file (native both pattern)
        foreach (SquadSkill skill in source.Skills)
        {
            Assert.True(
                result.Files.Any(f => f.RelativePath == $".agents/skills/{skill.Name}/SKILL.md" &&
                                      f.Target == "antigravity"),
                $"Skill '{skill.Name}' must render to .agents/skills/{skill.Name}/SKILL.md");
        }
    }

    /// <summary>Native rendering emits no role-prefixed collision files.</summary>
    [Fact]
    public async Task RenderAsync_Antigravity_Native_OmitsRolePrefixForCollisions()
    {
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Native dual emission: no role-prefixed collisions (no .agents/skills/role-* files)
        string[] rolePrefixedPaths = result.Files
            .Where(f => f.RelativePath.Contains("/role-", StringComparison.Ordinal))
            .Select(f => f.RelativePath)
            .ToArray();
        Assert.True(
            rolePrefixedPaths.Length == 0,
            $"Native renderer must not emit role-prefixed collision files. Found: {string.Join(", ", rolePrefixedPaths)}");
    }

    /// <summary>The conductor's native <c>agent.md</c> carries name, model and both tool-enable keys.</summary>
    [Fact]
    public async Task RenderAsync_Antigravity_Native_AgentFrontmatterIncludesNativeKeys()
    {
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Conductor agent must render as a native agent.md with required frontmatter keys
        SquadDeploymentFile conductorFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".agents/agents/conductor/agent.md");

        YamlMappingNode frontmatter = ReadFrontmatter(conductorFile);

        // Must have name
        string conductorName = RequireScalar(frontmatter, "name");
        Assert.Equal("conductor", conductorName);

        // Must have model (from antigravity profiles)
        string model = RequireScalar(frontmatter, "model");
        Assert.Matches("^(inherit|flash|pro)$", model);

        // Must have enable_write_tools and enable_subagent_tools
        Assert.True(
            frontmatter.Children.ContainsKey(new YamlScalarNode("enable_write_tools")),
            "Conductor must have enable_write_tools key");
        Assert.True(
            frontmatter.Children.ContainsKey(new YamlScalarNode("enable_subagent_tools")),
            "Conductor must have enable_subagent_tools key");
    }

    /// <summary>The conductor renders with <c>mainAgent: true</c> (D5).</summary>
    [Fact]
    public async Task RenderAsync_Antigravity_Native_ConductorHasMainAgentFlag()
    {
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Conductor must render as a native agent and have mainAgent: true (D5)
        SquadDeploymentFile conductorFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".agents/agents/conductor/agent.md");

        YamlMappingNode frontmatter = ReadFrontmatter(conductorFile);
        YamlNode? mainAgentNode = OptionalScalar(frontmatter, "mainAgent");
        Assert.True(
            mainAgentNode is not null,
            "Conductor must have mainAgent key");
        string mainAgentValue = Assert.IsType<YamlScalarNode>(mainAgentNode).Value
                                ?? throw new InvalidOperationException("mainAgent scalar is null");
        Assert.True(
            string.Equals("true", mainAgentValue, StringComparison.OrdinalIgnoreCase),
            $"Conductor mainAgent must be 'true', got '{mainAgentValue}'");
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_InvestigatorProfileHasNoWriteTools()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D4/D10: Investigator profile (process.execute allow, filesystem.write deny)
        // must not include write tools in the narrowed tools list
        List<SquadAgent> investigatorAgents = source.Agents
            .Where(a => a.CapabilityProfile == "investigator")
            .ToList();

        // Expected at least one agent with investigator capability profile for this test
        Assert.NotEmpty(investigatorAgents);

        foreach (SquadAgent agent in investigatorAgents)
        {
            // Native renderer must emit agent.md for every agent
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");
            Assert.True(
                agentFile is not null);

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            YamlNode? toolsNode = OptionalScalar(frontmatter, "tools");

            // Tools list must not contain write_to_file / replace_file_content / multi_replace_file_content
            if (toolsNode is YamlSequenceNode toolsSeq)
            {
                string[] tools = toolsSeq.Children
                    .OfType<YamlScalarNode>()
                    .Select(n => n.Value ?? string.Empty)
                    .ToArray();

                Assert.False(
                    tools.Contains("write_to_file", StringComparer.Ordinal),
                    $"Investigator '{agent.Name}' must not have write_to_file tool");
                Assert.False(
                    tools.Contains("replace_file_content", StringComparer.Ordinal),
                    $"Investigator '{agent.Name}' must not have replace_file_content tool");
                Assert.False(
                    tools.Contains("multi_replace_file_content", StringComparer.Ordinal),
                    $"Investigator '{agent.Name}' must not have multi_replace_file_content tool");
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_DocumentationProfileGrantsRunCommand()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D4/D10: Documentation profile (filesystem.write allow, process.execute allow)
        // must include run_command in the tools list, and must not degrade as not isolable
        List<SquadAgent> docProfileAgents = source.Agents
            .Where(a => a.CapabilityProfile == "documentation")
            .ToList();

        // Expected at least one agent with documentation capability profile for this test
        Assert.NotEmpty(docProfileAgents);

        foreach (SquadAgent agent in docProfileAgents)
        {
            // Native renderer must emit agent.md for every agent
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");
            Assert.True(
                agentFile is not null);

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            YamlNode? toolsNode = OptionalScalar(frontmatter, "tools");

            // The tools sequence must exist before its contents are asserted; a missing section
            // must fail here rather than let the grant checks below pass vacuously.
            Assert.True(
                toolsNode is YamlSequenceNode,
                $"Documentation agent '{agent.Name}' must render a tools sequence in agent.md frontmatter.");
            YamlSequenceNode toolsSeq = (YamlSequenceNode)toolsNode!;

            // Tools list must contain run_command alongside write_to_file
            string[] tools = toolsSeq.Children
                .OfType<YamlScalarNode>()
                .Select(n => n.Value ?? string.Empty)
                .ToArray();

            Assert.True(
                tools.Contains("run_command", StringComparer.Ordinal),
                $"Documentation agent '{agent.Name}' must have run_command tool");
            Assert.True(
                tools.Contains("write_to_file", StringComparer.Ordinal),
                $"Documentation agent '{agent.Name}' must have write_to_file tool");
            Assert.DoesNotContain(
                result.Degradations,
                d => d.CanonicalIdentity == agent.Name && d.Code == "capability-not-isolable");
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_EnableWriteToolsAssertedPerCapabilityProfile()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        HashSet<string> expectedTrueProfiles =
        [
            "architect", "documentation", "investigator", "product-planning", "reviewer", "root-fixer", "worker",
            "publishing-worker"
        ];
        HashSet<string> expectedFalseProfiles = ["orchestrator", "read-only"];

        List<SquadAgent> trueAgents = [];
        List<SquadAgent> falseAgents = [];

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            bool filesystemWriteAllow =
                profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
                writeDecision == SquadPermissionDecision.Allow;
            bool processExecuteAllow =
                profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision) &&
                execDecision == SquadPermissionDecision.Allow;
            bool expectedEnableWrite = filesystemWriteAllow || processExecuteAllow;

            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");
            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            string enableWriteTools = RequireScalar(frontmatter, "enable_write_tools");

            if (expectedEnableWrite)
            {
                trueAgents.Add(agent);
                Assert.True(
                    expectedTrueProfiles.Contains(agent.CapabilityProfile),
                    $"Agent '{agent.Name}' with profile '{agent.CapabilityProfile}' has enableWriteTools true but profile was not in expectedTrueProfiles");
                Assert.Equal("true", enableWriteTools);
            }
            else
            {
                falseAgents.Add(agent);
                Assert.True(
                    expectedFalseProfiles.Contains(agent.CapabilityProfile),
                    $"Agent '{agent.Name}' with profile '{agent.CapabilityProfile}' has enableWriteTools false but profile was not in expectedFalseProfiles");
                Assert.Equal("false", enableWriteTools);
            }
        }

        Assert.NotEmpty(trueAgents);
        Assert.NotEmpty(falseAgents);
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_WriteToFileToolAssertedPerCapabilityProfile()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        HashSet<string> expectedWriteToFileProfiles =
            ["architect", "documentation", "product-planning", "root-fixer", "worker", "publishing-worker"];

        List<SquadAgent> writeAgents = [];
        List<SquadAgent> nonWriteAgents = [];

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            bool filesystemWriteAllow =
                profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
                writeDecision == SquadPermissionDecision.Allow;

            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");
            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            YamlNode? toolsNode = OptionalScalar(frontmatter, "tools");

            string[] tools = toolsNode is YamlSequenceNode toolsSeq
                ? toolsSeq.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? string.Empty).ToArray()
                : [];

            if (filesystemWriteAllow)
            {
                writeAgents.Add(agent);
                Assert.True(
                    expectedWriteToFileProfiles.Contains(agent.CapabilityProfile),
                    $"Agent '{agent.Name}' with profile '{agent.CapabilityProfile}' has filesystem.write: allow but profile was not in expectedWriteToFileProfiles");
                Assert.Contains("write_to_file", tools);
            }
            else
            {
                nonWriteAgents.Add(agent);
                Assert.DoesNotContain("write_to_file", tools);
            }
        }

        Assert.NotEmpty(writeAgents);
        Assert.NotEmpty(nonWriteAgents);
    }

    /// <summary>
    /// Agents with process.execute: allow and filesystem.write: ask or deny (e.g. investigator and
    /// reviewer profiles) receive a capability-not-isolable degradation record naming the granted
    /// shell tools (run_command) and withheld write tools (write_to_file, replace_file_content, multi_replace_file_content).
    /// Agents with filesystem.write: allow or process.execute: deny do not receive this degradation.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Antigravity_Native_CapabilityNotIsolableDegradationRecordPresent()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        string[] grantedShellTools = ["run_command"];
        string[] withheldWriteTools = ["write_to_file", "replace_file_content", "multi_replace_file_content"];

        List<string> expectedAgents = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile p = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                bool exec = p.Permissions.TryGetValue("process.execute", out SquadPermissionDecision e) &&
                            e == SquadPermissionDecision.Allow;
                bool write = p.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision w) &&
                             w == SquadPermissionDecision.Allow;
                return exec && !write;
            })
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expectedAgents);

        foreach (SquadAgent agent in source.Agents)
        {
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            bool executeAllowed =
                profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec) &&
                exec == SquadPermissionDecision.Allow;
            bool writeAllowed =
                profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write) &&
                write == SquadPermissionDecision.Allow;

            SquadDegradationRecord? record = result.Degradations.FirstOrDefault(d =>
                d.CanonicalIdentity == agent.Name && d.Code == "capability-not-isolable");

            if (executeAllowed && !writeAllowed)
            {
                Assert.NotNull(record);
                Assert.Equal("antigravity", record.Target);
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

    /// <summary>Every agent with a non-empty <c>delegates-to</c> roster gets subagent invocation enabled (D12).</summary>
    [Fact]
    public async Task RenderAsync_Antigravity_Native_DelegationEmittedPerDelegatesToRoster()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D12: Agents with non-empty delegates-to must have invoke_subagent + enable_subagent_tools
        List<SquadAgent> delegatingAgents = source.Agents.Where(a => a.DelegatesTo.Count > 0).ToList();
        Assert.NotEmpty(delegatingAgents);

        foreach (SquadAgent agent in delegatingAgents)
        {
            // Native renderer must emit agent.md for every agent
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);

            // Must have enable_subagent_tools: true
            string enableSubagentTools = RequireScalar(frontmatter, "enable_subagent_tools");
            Assert.True(
                string.Equals("true", enableSubagentTools, StringComparison.OrdinalIgnoreCase),
                $"Delegating agent '{agent.Name}' enable_subagent_tools must be 'true', got '{enableSubagentTools}'");

            // Must have invoke_subagent in tools
            YamlNode? toolsNodeOptional = OptionalScalar(frontmatter, "tools");
            Assert.True(
                toolsNodeOptional is not null,
                $"Delegating agent '{agent.Name}' must have tools list");
            YamlSequenceNode toolsSeq = Assert.IsType<YamlSequenceNode>(toolsNodeOptional);
            string[] tools = toolsSeq.Children
                .OfType<YamlScalarNode>()
                .Select(n => n.Value ?? string.Empty)
                .ToArray();

            Assert.Contains("invoke_subagent", tools);

            // If agent is orchestrator/conductor, must also have manage_subagents
            if (agent.Name == "conductor" || agent.Name == "orchestrator")
            {
                Assert.Contains("manage_subagents", tools);
            }
        }

        // D12: Agents with empty delegates-to must NOT have invoke_subagent or enable_subagent_tools: true
        List<SquadAgent> nonDelegatingAgents = source.Agents.Where(a => a.DelegatesTo.Count == 0).ToList();
        Assert.NotEmpty(nonDelegatingAgents);

        foreach (SquadAgent agent in nonDelegatingAgents)
        {
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            YamlNode? toolsNode = OptionalScalar(frontmatter, "tools");

            if (toolsNode is YamlSequenceNode toolsSeq)
            {
                string[] tools = toolsSeq.Children
                    .OfType<YamlScalarNode>()
                    .Select(n => n.Value ?? string.Empty)
                    .ToArray();

                Assert.False(
                    tools.Contains("invoke_subagent", StringComparer.Ordinal),
                    $"Non-delegating agent '{agent.Name}' must not have invoke_subagent");
            }

            // enable_subagent_tools should not be true for non-delegating agents
            YamlNode? enableSubagentNode = OptionalScalar(frontmatter, "enable_subagent_tools");
            if (enableSubagentNode is YamlScalarNode enableSubagentScalar)
            {
                Assert.False(
                    string.Equals("true", enableSubagentScalar.Value, StringComparison.OrdinalIgnoreCase),
                    $"Non-delegating agent '{agent.Name}' should not have enable_subagent_tools: true");
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_OmitsCamelCaseOptionalFields()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D7: Defer camelCase optional fields (hidden, inheritMcp, commandExecutionPolicy)
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);

            // None of these deferred keys should appear
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("hidden")));
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("inheritMcp")),
                $"Agent '{agent.Name}' must not have 'inheritMcp' key (deferred per D7)");
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("commandExecutionPolicy")),
                $"Agent '{agent.Name}' must not have 'commandExecutionPolicy' key (deferred per D7)");
        }
    }

    /// <summary>
    /// Pins rendered Antigravity models by agent identity so planners and reviewers use
    /// Pro while workers and orchestration remain on Flash, independently of models.yml.
    /// </summary>
    [Fact]
    public async Task AntigravityPlanningAndReviewAgentsRunOnProAndOtherAgentsOnFlash()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadAgent architect = Assert.Single(source.Agents, agent => agent.Name == "architect");
        Assert.Equal("architect", architect.ModelProfile);

        SquadAgent productOwner = Assert.Single(source.Agents, agent => agent.Name == "product-owner");
        Assert.Equal("architect", productOwner.ModelProfile);

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            string model = RequireScalar(frontmatter, "model");
            string expectedModel = agent.Name is "architect" or "product-owner" or
                "sql-database-architect" or "bug-crusher-investigator" or "code-reviewer" or
                "review-lens" or "review-triage"
                ? "pro"
                : "flash";

            Assert.True(
                string.Equals(expectedModel, model, StringComparison.Ordinal),
                $"Agent '{agent.Name}' (profile '{agent.ModelProfile}') model must be '{expectedModel}', got '{model}'.");
            Assert.False(
                model.Contains("flash", StringComparison.OrdinalIgnoreCase) &&
                model.Contains('-', StringComparison.Ordinal),
                $"Agent '{agent.Name}' model must not use flash-lite or flash_lite spelling, got '{model}'");
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_ReasoningEffortMatchesPerProfileMapping()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D9: reasoning_effort must match approved per-profile mapping
        Dictionary<string, string> expectedReasoningEffort = new(StringComparer.Ordinal)
        {
            { "architect", "high" },
            { "deep-planning", "high" },
            { "reviewer", "high" },
            { "general", "medium" },
            { "fast", "low" },
            { "orchestration", "minimal" }
        };

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);

            if (expectedReasoningEffort.TryGetValue(agent.ModelProfile, out string? expectedLevel))
            {
                string? actualLevel = OptionalScalar(frontmatter, "reasoning_effort")?.ToString();
                Assert.True(
                    string.Equals(expectedLevel, actualLevel, StringComparison.Ordinal),
                    $"Agent '{agent.Name}' (profile '{agent.ModelProfile}') reasoning_effort must be '{expectedLevel}', got '{actualLevel}'");
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_InconclusiveToolsWithheld()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D12: These tools are withheld pending live verification
        string[] inconclusivelTools =
            ["grep_search", "replace_file_content", "multi_replace_file_content", "search_web", "read_url_content"];

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            YamlNode? toolsNode = OptionalScalar(frontmatter, "tools");

            if (toolsNode is YamlSequenceNode toolsSeq)
            {
                string[] tools = toolsSeq.Children
                    .OfType<YamlScalarNode>()
                    .Select(n => n.Value ?? string.Empty)
                    .ToArray();

                foreach (string inconclusive in inconclusivelTools)
                {
                    Assert.False(
                        tools.Contains(inconclusive, StringComparer.Ordinal),
                        $"Agent '{agent.Name}' must not have '{inconclusive}' tool (withheld per D12)");
                }
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_UnenforcedDelegatesToRecordsPermissionNotExpressible()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D12-delegate: Agents with non-empty delegates-to have an unenforceable roster,
        // which should be recorded as permission-not-expressible degradation
        List<SquadAgent> delegatingAgents = source.Agents.Where(a => a.DelegatesTo.Count > 0).ToList();
        Assert.NotEmpty(delegatingAgents);

        foreach (SquadAgent agent in delegatingAgents)
        {
            // Should have a permission-not-expressible degradation for the unenforceable roster
            SquadDegradationRecord? delegateDegradation = result.Degradations
                .FirstOrDefault(d => d.CanonicalIdentity == agent.Name &&
                                     d.Code == "permission-not-expressible" &&
                                     d.Details?.Contains("delegates-to", StringComparison.OrdinalIgnoreCase) == true);

            Assert.True(
                delegateDegradation is not null,
                $"Agent '{agent.Name}' with delegates-to roster must have permission-not-expressible degradation recording the unenforceable roster");
        }
    }

    [Fact]
    public async Task SquadRendererRegistry_IncludesAntigravityInNativeTargetSet()
    {
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);

        Assert.Contains(SquadTarget.Antigravity, registry.SupportedTargets);

        // Native target validation applies to Antigravity: rendering must succeed without
        // triggering native validation errors (no role- prefixes, single projection per identity)
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SquadDeploymentPlan_IdentityFromRelativePathHandlesAgentMd()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // SquadDeploymentPlan.IdentityFromRelativePath must resolve a bare agent.md
        // to its parent directory name (e.g., ".agents/agents/conductor/agent.md" → "conductor")
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile agentFile =
                result.Files.First(f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            string identity = SquadDeploymentPlan.IdentityFromRelativePath(agentFile.RelativePath);
            Assert.Equal(agent.Name, identity);
        }
    }

    /// <summary>
    /// Every agent the source entitles to <c>kyber-weave</c> renders it in its own frontmatter, in
    /// the one entry shape live-probed against <c>agy</c> 1.4.12, and every agent the source does
    /// not entitle renders no <c>mcpServers</c> key at all.
    /// </summary>
    /// <remarks>
    /// The roster is computed from the loaded capability profiles by
    /// <see cref="SquadMcpGrantProjection"/>, never from the renderer, so a renderer that re-added
    /// the pure-orchestrator carve-out fails here rather than agreeing with its own copy of the
    /// rule. <c>conductor</c>'s presence is asserted explicitly as well, because that one agent is
    /// the whole point of ADR 0049: <c>kyber-weave</c> carries <c>agent_coding_standards</c>, and
    /// the role that assigns work is the role that needs it.
    ///
    /// The shape is asserted structurally rather than by string match because it is the contract.
    /// The probe called <c>{name, command, args}</c> entries successfully; the alternative
    /// single-key map spelling (<c>- kyber-weave: {command, args}</c>) listed and ran but was never
    /// called, so it is not the shape this suite accepts.
    /// </remarks>
    [Fact]
    public async Task EveryReadEntitledAgentRendersTheKyberWeaveMcpServerInTheCallVerifiedShape()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        IReadOnlySet<string> entitled = SquadMcpGrantProjection.KyberWeaveEntitledAgents(
            source,
            SquadTarget.Antigravity);

        SquadAgent conductor = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        Assert.Contains(conductor.Name, entitled, StringComparer.Ordinal);

        (string Command, string[] Args) declaration = DeclaredKyberWeaveServer(source);

        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                file => file.RelativePath == $".agents/agents/{agent.Name}/agent.md");
            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);

            if (!entitled.Contains(agent.Name))
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("mcpServers")),
                    $"Agent '{agent.Name}' (capability profile '{agent.CapabilityProfile}') renders an "
                    + "'mcpServers' key, but the source withholds kyber-weave from it.");
                continue;
            }

            YamlNode servers = RequireNode(frontmatter, "mcpServers");
            YamlSequenceNode entries = Assert.IsType<YamlSequenceNode>(servers);
            YamlMappingNode entry = Assert.IsType<YamlMappingNode>(Assert.Single(entries.Children));

            // The verified entry shape, and only that shape: an entry that also carried the server
            // name as its own key would be the spelling the probe never called.
            string[] keys = [.. entry.Children.Keys
                .Select(key => (key as YamlScalarNode)?.Value ?? string.Empty)];
            Assert.Equal(["name", "command", "args"], keys);
            Assert.Equal("kyber-weave", RequireScalar(entry, "name"));
            Assert.Equal(declaration.Command, RequireScalar(entry, "command"));
            Assert.Equal(declaration.Args, RequireSequenceValues(entry, "args"));
        }
    }

    /// <summary>
    /// An agent whose profile denies <c>filesystem.read</c> renders no MCP server and carries a
    /// <c>permission-not-expressible</c> record naming the server it lost and the rule that lost it,
    /// while a granted agent beside it carries no withholding claim.
    /// </summary>
    /// <remarks>
    /// Two outcomes, two records. A blanket "MCP is not expressible here" sentence would have made
    /// a deliberately granted agent indistinguishable from a withheld one in the only place a
    /// review or a <c>squad doctor</c> run reads either, which is how the pre-ADR-0049 renderer
    /// reported every agent as degraded while granting nobody.
    /// </remarks>
    [Fact]
    public async Task AnAgentWhoseProfileDeniesFileReadRendersNoMcpServerAndRecordsTheWithholding()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithFileReadDeniedAgent("review-lens");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        SquadAgent denied = Assert.Single(source.Agents, agent => agent.Name == "review-lens");
        Assert.False(
            SquadMcpGrantProjection.GrantsKyberWeave(source, denied, SquadTarget.Antigravity),
            "The read-denied fixture still entitles its agent, so the fail-closed branch is not "
            + "exercised by this test.");

        SquadRenderResult result = await new AntigravityRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        YamlMappingNode withheldFrontmatter = ReadFrontmatter(Assert.Single(
            result.Files,
            file => file.RelativePath == $".agents/agents/{denied.Name}/agent.md"));
        Assert.False(
            withheldFrontmatter.Children.ContainsKey(new YamlScalarNode("mcpServers")),
            $"Agent '{denied.Name}' denies 'filesystem.read' and must render no 'mcpServers' entry.");

        SquadDegradationRecord withholding = Assert.Single(
            result.Degradations,
            record => string.Equals(record.CanonicalIdentity, denied.Name, StringComparison.Ordinal) &&
                      string.Equals(record.Code, "permission-not-expressible", StringComparison.Ordinal));
        Assert.NotNull(withholding.Details);
        Assert.Contains("kyber-weave", withholding.Details, StringComparison.Ordinal);
        Assert.Contains("withheld", withholding.Details, StringComparison.Ordinal);
        Assert.Contains("filesystem.read", withholding.Details, StringComparison.Ordinal);

        SquadAgent granted = Assert.Single(source.Agents, agent => agent.Name == "architect");
        Assert.True(
            SquadMcpGrantProjection.GrantsKyberWeave(source, granted, SquadTarget.Antigravity),
            "The read-denied fixture must leave every other agent entitled, or the granted half of "
            + "this test proves nothing.");
        foreach (SquadDegradationRecord record in result.Degradations.Where(record =>
                     string.Equals(record.CanonicalIdentity, granted.Name, StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("kyber-weave", record.Details ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A squad that declares MCP servers but not <c>kyber-weave</c> grants it to nobody: every
    /// entitled agent renders no <c>mcpServers</c> key, and no record claims a server was withheld.
    /// </summary>
    /// <remarks>
    /// This is the declaration half of the fail-closed rule, and it is a different question from
    /// the profile half the read-denied test above covers. The profiles here entitle every agent;
    /// only the <c>mcp.json</c> is short of the server. Without this case a renderer that resolved
    /// whichever server the declaration happened to name first — or one that emitted an empty
    /// <c>mcpServers: []</c> rather than omitting the key — would satisfy every other Antigravity
    /// assertion in this file, and on this harness an explicit empty list reads as "exclude every
    /// MCP server" rather than as an absent key.
    ///
    /// Nothing is reported as withheld, because nothing was there to withhold: the profiles entitled
    /// every agent and only the declaration was short. What is reported is the configuration gap
    /// itself, in one record per agent, naming the missing server. Without that sentence a renamed or
    /// dropped `mcp.json` entry would cost every agent the grant in silence.
    /// </remarks>
    [Fact]
    public async Task AnMcpDeclarationWithoutTheKyberWeaveServerGrantsItToNobodyAndClaimsNoWithholding()
    {
        using McpDeclarationCorpusFixture fixture = McpDeclarationCorpusFixture.WithServers("codegraph");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        Assert.Equal(["codegraph"], SquadMcpDeclaration.ServerNames(source));

        // The profiles still entitle agents, so a passing render cannot be explained by an empty
        // entitlement set.
        IReadOnlySet<string> entitled = SquadMcpGrantProjection.KyberWeaveEntitledAgents(
            source,
            SquadTarget.Antigravity);
        Assert.NotEmpty(entitled);

        SquadRenderResult result = await new AntigravityRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project,
            PreloadedSource: source));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            YamlMappingNode frontmatter = ReadFrontmatter(Assert.Single(
                result.Files,
                file => file.RelativePath == $".agents/agents/{agent.Name}/agent.md"));
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("mcpServers")),
                $"Agent '{agent.Name}' renders an 'mcpServers' key although the squad declares no "
                + "'kyber-weave' server. An omitted key is the only fail-closed answer: on Antigravity "
                + "an explicit empty list excludes every server rather than inheriting nothing.");
        }

        Assert.All(
            result.Degradations,
            record => Assert.DoesNotContain(
                "withheld",
                record.Details ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));

        // Exactly one record per agent names the missing server: the grant gap is per-agent
        // state, so an existence-only assertion — or a split shell/missing pair across two
        // records — would let a partial render pass. One record carries every gap that agent has.
        foreach (SquadAgent agent in source.Agents)
        {
            Assert.Single(
                result.Degradations.Where(record => string.Equals(
                    record.CanonicalIdentity,
                    agent.Name,
                    StringComparison.Ordinal)),
                record => record.Details is not null
                          && record.Details.Contains("declares no 'kyber-weave' server", StringComparison.Ordinal)
                          && record.Details.Contains("no per-agent grant could be emitted", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A missing server and a denying profile are two independent reasons one agent has no grant,
    /// so the one record names both: the renamed-or-dropped declaration and the profile that would
    /// still withhold the grant after the server came back.
    /// </summary>
    /// <remarks>
    /// Reporting only the declaration would hide the entitlement: an operator who restores the
    /// server would watch this agent stay grantless with no record saying why. Reporting only the
    /// entitlement would hide the declaration every other agent's record names. The combined
    /// sentence is the only outcome that leaves neither gap silent.
    /// </remarks>
    [Fact]
    public async Task AMissingServerAndADenyingProfileAreBothNamedInOneRecord()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithServersAndFileReadDeniedAgent("review-lens", "codegraph");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        SquadAgent denied = Assert.Single(source.Agents, agent => agent.Name == "review-lens");
        Assert.False(
            SquadMcpGrantProjection.GrantsKyberWeave(source, denied, SquadTarget.Antigravity),
            "The read-denied fixture still entitles its agent, so the combined branch is not exercised.");

        SquadRenderResult result = await new AntigravityRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project,
            PreloadedSource: source));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDegradationRecord record = Assert.Single(
            result.Degradations,
            record => string.Equals(record.CanonicalIdentity, denied.Name, StringComparison.Ordinal) &&
                      string.Equals(record.Code, "permission-not-expressible", StringComparison.Ordinal));
        Assert.NotNull(record.Details);
        Assert.Contains("declares no 'kyber-weave' server", record.Details, StringComparison.Ordinal);
        Assert.Contains("does not allow 'filesystem.read'", record.Details, StringComparison.Ordinal);
        Assert.Contains("even with the server declared", record.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// A shell gap, a missing server, and a denying profile are three independent reasons one
    /// agent has no grant, so the one record names all three.
    /// </summary>
    /// <remarks>
    /// The D10 early return carries the shell-escape record past the grant checks. Without the
    /// merge, a triple-gap agent's record would name the shell gap and the missing server while
    /// hiding the denying profile — the same silent loss the missing-server sentence was added
    /// to prevent, one path earlier.
    /// </remarks>
    [Fact]
    public async Task AShellGapAndAMissingServerAndADenyingProfileAreAllNamedInOneRecord()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithServersAndFileReadDeniedAgent("review-lens", "codegraph");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        SquadAgent denied = Assert.Single(source.Agents, agent => agent.Name == "review-lens");

        // Promote the read-denied profile to a shell gap as well: execute allowed while write
        // stays denied. The hand edit lands after load, so no source validation stands between
        // the profile and the renderer.
        Dictionary<string, SquadCapabilityProfile> profiles = new(
            source.CapabilityProfiles.Profiles,
            StringComparer.Ordinal);
        SquadCapabilityProfile baseProfile = profiles[denied.CapabilityProfile];
        Dictionary<string, SquadPermissionDecision> permissions = new(
            baseProfile.Permissions,
            StringComparer.Ordinal)
        {
            ["process.execute"] = SquadPermissionDecision.Allow,
            ["filesystem.write"] = SquadPermissionDecision.Deny,
        };
        profiles[denied.CapabilityProfile] = baseProfile with { Permissions = permissions };
        source = source with
        {
            CapabilityProfiles = source.CapabilityProfiles with { Profiles = profiles },
        };

        SquadRenderResult result = await new AntigravityRenderer().RenderAsync(new SquadRenderRequest(
            fixture.ProductRoot,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project,
            PreloadedSource: source));
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDegradationRecord record = Assert.Single(
            result.Degradations,
            record => string.Equals(record.CanonicalIdentity, denied.Name, StringComparison.Ordinal) &&
                      string.Equals(record.Code, "capability-not-isolable", StringComparison.Ordinal));
        Assert.NotNull(record.Details);
        Assert.Contains("shell redirection", record.Details, StringComparison.Ordinal);
        Assert.Contains("declares no 'kyber-weave' server", record.Details, StringComparison.Ordinal);
        Assert.Contains("does not allow 'filesystem.read'", record.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// A declaration that cannot be resolved to a usable launch definition fails closed: no entitled
    /// agent renders <c>mcpServers</c>, rather than every agent rendering an entry built from a
    /// partial or guessed definition.
    /// </summary>
    /// <remarks>
    /// Each row is one rejection branch of <c>ResolveUniversalMcpServer</c>, driven through a
    /// hand-built <see cref="SquadSource"/>: the missing <c>mcpServers</c> key, a non-object
    /// <c>mcpServers</c>, a non-object entry, a missing, non-string, or blank <c>command</c>, a
    /// missing or non-array <c>args</c>, and an <c>args</c> element that is null, a number, a
    /// boolean, an object, or an array.
    ///
    /// The hand-built source is necessary rather than lazy. <see cref="SquadSourceLoader"/> rejects
    /// every one of these states at load time (<c>ParseMcp</c> and <c>ValidateMcpServer</c> demand an
    /// object <c>mcpServers</c>, an object <c>kyber-weave</c> entry, a non-empty string
    /// <c>command</c>, and a string-array <c>args</c>), so no on-disk corpus can reach them. They
    /// exist to keep a hand-built or future-loaded source from rendering a server the operator never
    /// declared, and until this theory they were asserted only in prose.
    ///
    /// The first row is the positive control: it is the same well-formed declaration the shipped
    /// corpus carries, so it proves the hand-built path really does reach the renderer and that a
    /// resolved declaration still grants. Without it, a renderer that had stopped granting anything
    /// would satisfy every rejection row.
    ///
    /// The <c>args</c> element rows exist because the renderer asks each element its
    /// <see cref="JsonElement.ValueKind"/> before reading it, exactly as the sibling <c>command</c>
    /// branch does, and a non-string element then rejects the whole declaration. Reading the element
    /// with <see cref="JsonElement.GetString"/> alone answers only for a string or a null and raises
    /// for every other kind, so the null row would survive that regression while a number, a
    /// boolean, an object, or an array would escape <c>RenderAsync</c> as a failed render — an
    /// exception in place of the withheld grant this contract promises. Each kind earns its own row
    /// for that reason: one representative would pass again the moment the guard was narrowed to the
    /// kinds it happens to cover. The loader rejects every one of these states first, so the rows
    /// pin a hand-built or future-loaded source rather than a shipped corpus.
    /// </remarks>
    [Theory]
    [InlineData(
        "well-formed declaration",
        """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", "."]}}}""",
        true)]
    [InlineData("no mcpServers key", "{}", false)]
    [InlineData("non-object mcpServers", """{"mcpServers": []}""", false)]
    [InlineData("non-object entry", """{"mcpServers": {"kyber-weave": "kyber-weave-mcp"}}""", false)]
    [InlineData("missing command", """{"mcpServers": {"kyber-weave": {"args": []}}}""", false)]
    [InlineData("non-string command", """{"mcpServers": {"kyber-weave": {"command": 7, "args": []}}}""", false)]
    [InlineData("blank command", """{"mcpServers": {"kyber-weave": {"command": "   ", "args": []}}}""", false)]
    [InlineData("missing args", """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp"}}}""", false)]
    [InlineData(
        "non-array args",
        """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": "--repo-root"}}}""",
        false)]
    [InlineData(
        "null args element",
        """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", null]}}}""",
        false)]
    [InlineData(
        "numeric args element",
        """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", 7]}}}""",
        false)]
    [InlineData(
        "true args element",
        """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", true]}}}""",
        false)]
    [InlineData(
        "false args element",
        """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", false]}}}""",
        false)]
    [InlineData(
        "object args element",
        """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", {}]}}}""",
        false)]
    [InlineData(
        "array args element",
        """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", []]}}}""",
        false)]
    public async Task ADeclarationThatCannotBeResolvedGrantsNoMcpServerAndRecordsNoWithholding(
        string caseName,
        string mcpConfiguration,
        bool grants)
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        IReadOnlySet<string> entitled = SquadMcpGrantProjection.KyberWeaveEntitledAgents(
            source,
            SquadTarget.Antigravity);
        Assert.NotEmpty(entitled);

        using JsonDocument declaration = JsonDocument.Parse(mcpConfiguration);
        SquadSource configured = source with { McpConfiguration = declaration.RootElement.Clone() };

        SquadRenderResult result = await new AntigravityRenderer().RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project,
            PreloadedSource: configured));
        Assert.True(result.Success, $"[{caseName}] {string.Join("; ", result.Errors)}");

        foreach (string agentName in entitled)
        {
            YamlMappingNode frontmatter = ReadFrontmatter(Assert.Single(
                result.Files,
                file => file.RelativePath == $".agents/agents/{agentName}/agent.md"));
            bool carriesEntry = frontmatter.Children.ContainsKey(new YamlScalarNode("mcpServers"));

            Assert.True(
                carriesEntry == grants,
                $"[{caseName}] Agent '{agentName}' "
                + (carriesEntry ? "renders" : "omits")
                + " 'mcpServers', but this declaration must "
                + (grants ? "grant" : "withhold")
                + " it. An unresolvable declaration yields no entry at all — never one built from a "
                + "partial definition, and never an empty '[]', which Antigravity reads as exclude-all.");

            if (!grants)
            {
                continue;
            }

            YamlSequenceNode entries = Assert.IsType<YamlSequenceNode>(
                RequireNode(frontmatter, "mcpServers"));
            Assert.NotEmpty(entries.Children);
        }

        if (grants)
        {
            return;
        }

        foreach (SquadDegradationRecord record in result.Degradations)
        {
            Assert.DoesNotContain("withheld", record.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kyber-weave", record.Details ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The launch definition the squad's own <c>mcp.json</c> declares for <c>kyber-weave</c>, read
    /// from the loaded source so the rendered entry is compared against the declaration rather
    /// than against a literal the renderer could have drifted from.
    /// </summary>
    private static (string Command, string[] Args) DeclaredKyberWeaveServer(SquadSource source)
    {
        Assert.True(
            source.McpConfiguration.TryGetProperty("mcpServers", out JsonElement servers),
            "The canonical squad declares no 'mcpServers', so there is no server to grant.");
        Assert.True(
            servers.TryGetProperty(SquadMcpGrantProjection.KyberWeaveServer, out JsonElement server),
            "The canonical squad declares no 'kyber-weave' server, so there is no grant to assert.");
        Assert.True(
            server.TryGetProperty("command", out JsonElement command),
            "The declared 'kyber-weave' server carries no 'command'.");
        Assert.True(
            server.TryGetProperty("args", out JsonElement args),
            "The declared 'kyber-weave' server carries no 'args'.");

        return (
            command.GetString() ?? string.Empty,
            [.. args.EnumerateArray().Select(arg => arg.GetString() ?? string.Empty)]);
    }

    private static YamlNode RequireNode(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"Frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        return value;
    }

    private static string[] RequireSequenceValues(YamlMappingNode node, string key)
    {
        YamlSequenceNode sequence = Assert.IsType<YamlSequenceNode>(RequireNode(node, key));
        return
        [
            .. sequence.Children.Select(child => Assert.IsType<YamlScalarNode>(child).Value
                ?? throw new InvalidOperationException($"Key '{key}' has a null sequence entry."))
        ];
    }
}
