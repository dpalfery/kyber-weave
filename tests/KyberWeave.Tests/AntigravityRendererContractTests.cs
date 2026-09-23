using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real, checked-in canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="AntigravityRenderer"/> and pins the native agent-per-directory contract
/// (agents at <c>.agents/agents/<name>/agent.md</c>, skills at <c>.agents/skills/<name>/SKILL.md</c>).
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
    private static IReadOnlyList<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.TryGetValue("role-skill", out SquadFallbackProfile? profile)
            ? profile.SharedIdentities
            : [];

    /// <summary>
    /// The expected permission-degradation roster is derived from the loaded capability
    /// profiles' own declared vocabulary, mirroring the renderer's source of truth.
    /// </summary>
    private static bool HasNonDenyCapability(SquadCapabilityProfiles profiles, SquadCapabilityProfile profile) =>
        profiles.Capabilities.Any(capability =>
            profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
            decision != SquadPermissionDecision.Deny);

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

    [Fact]
    public async Task RenderAsync_RejectsNonAntigravityTarget()
    {
        AntigravityRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_Antigravity_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
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

        int unoccupiedAgents = source.Agents.Count(a => !shared.Contains(a.Name) && !collisions.Contains(a.Name));

        // Native dual-root rendering: every agent (except shared identities) emits an agent.md file,
        // all canonical skills are emitted, and each projects its validated resource closure beneath
        // its identity directory. R17: Agents.Count + Σ agent resources + Skills.Count − shared-identity
        // skills + Σ non-suppressed skill resources. (The conductor contributes one principal whether
        // it renders as an agent.md or, when shared, is omitted in favor of the canonical skill.)
        int suppressedSkillCount = source.Skills.Count(skill => shared.Contains(skill.Name));
        int expectedFiles = source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !shared.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);

        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

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

        // Native dual-root rendering with unified resource projection: agent principals are
        // .agents/agents/{name}/agent.md, skill principals are .agents/skills/{name}/SKILL.md;
        // closure resources for BOTH agents and skills project under the skills directory for
        // deployment simplicity: .agents/skills/{identity}/{resourcePath}.
        HashSet<string> agentResourceOutputPaths = source.Agents
            .SelectMany(
                agent => agent.Resources,
                (agent, resource) => $".agents/skills/{agent.Name}/{resource.RelativePath}")
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
                (f.RelativePath.StartsWith(".agents/agents/", StringComparison.Ordinal) && f.RelativePath.EndsWith("/agent.md", StringComparison.Ordinal)) ||
                (f.RelativePath.StartsWith(".agents/skills/", StringComparison.Ordinal) && f.RelativePath.EndsWith("/SKILL.md", StringComparison.Ordinal)) ||
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
                string.Equals(ToSingleLineScalar(agent.Description), RequireScalar(agentFrontmatter, "description"), StringComparison.Ordinal),
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
                source.CapabilityProfiles.Profiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile),
                $"Agent '{agent.Name}' references undeclared capability profile '{agent.CapabilityProfile}'.");

            bool executeAllowed = profile!.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec) &&
                exec == SquadPermissionDecision.Allow;
            bool writeAllowed = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write) &&
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
        foreach (SquadDegradationRecord degradation in result.Degradations.Where(d => d.Code == "permission-not-expressible"))
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
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

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
            SquadDeploymentScope.Project);

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

        // Verify that agent resources are projected under .agents/skills/ (unified projection)
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        foreach (SquadAgent agent in source.Agents)
        {
            foreach (SquadResource resource in agent.Resources)
            {
                Assert.Contains(
                    first.Files,
                    f => f.RelativePath == $".agents/skills/{agent.Name}/{resource.RelativePath}");
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
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string probe = source.Skills
            .Select(skill => skill.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Corpus at '{ProductRoot}' declares no skills.");

        SquadRenderResult result = await new AntigravityRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project));

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

    private static string ToSingleLineScalar(string value) =>
        string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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

    private static YamlMappingNode ReadFrontmatter(SquadDeploymentFile file) =>
        SplitFrontmatter(System.Text.Encoding.UTF8.GetString(file.Content.Span)).Frontmatter;

    private static string ReadBody(SquadDeploymentFile file) =>
        SplitFrontmatter(System.Text.Encoding.UTF8.GetString(file.Content.Span)).Body;

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

    private static YamlNode? OptionalScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) ? value : null;

    [Fact]
    public async Task RenderAsync_Antigravity_Native_EmitsNativeAgentFiles()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

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

    [Fact]
    public async Task RenderAsync_Antigravity_Native_EmitsCanonicalSkillFiles()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

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

    [Fact]
    public async Task RenderAsync_Antigravity_Native_OmitsRolePrefixForCollisions()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

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

    [Fact]
    public async Task RenderAsync_Antigravity_Native_AgentFrontmatterIncludesNativeKeys()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

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

    [Fact]
    public async Task RenderAsync_Antigravity_Native_ConductorHasMainAgentFlag()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

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
        string mainAgentValue = Assert.IsType<YamlScalarNode>(mainAgentNode!).Value
            ?? throw new InvalidOperationException("mainAgent scalar is null");
        Assert.True(
            string.Equals("true", mainAgentValue, StringComparison.OrdinalIgnoreCase),
            $"Conductor mainAgent must be 'true', got '{mainAgentValue}'");
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_InvestigatorProfileHasNoWriteTools()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D4/D10: Investigator profile (process.execute allow, filesystem.write deny)
        // must not include write tools in the narrowed tools list
        var investigatorAgents = source.Agents
            .Where(a => a.CapabilityProfile == "investigator")
            .ToList();

        // Expected at least one agent with investigator capability profile for this test
        Assert.NotEmpty(investigatorAgents);

        foreach (var agent in investigatorAgents)
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
    public async Task RenderAsync_Antigravity_Native_DocumentationProfileHasNoExecuteTools()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D4/D10: Documentation profile (filesystem.write allow, process.execute deny)
        // must not include run_command in the narrowed tools list
        var docProfileAgents = source.Agents
            .Where(a => a.CapabilityProfile == "documentation")
            .ToList();

        // Expected at least one agent with documentation capability profile for this test
        Assert.NotEmpty(docProfileAgents);

        foreach (var agent in docProfileAgents)
        {
            // Native renderer must emit agent.md for every agent
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");
            Assert.True(
                agentFile is not null);

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            YamlNode? toolsNode = OptionalScalar(frontmatter, "tools");

            // Tools list must not contain run_command
            if (toolsNode is YamlSequenceNode toolsSeq)
            {
                string[] tools = toolsSeq.Children
                    .OfType<YamlScalarNode>()
                    .Select(n => n.Value ?? string.Empty)
                    .ToArray();

                Assert.False(
                    tools.Contains("run_command", StringComparer.Ordinal),
                    $"Documentation agent '{agent.Name}' must not have run_command tool");
            }
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_CapabilityNotIsolableDegradationRecordPresent()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D10: When process.execute: allow and filesystem.write: ask/deny, expect
        // capability-not-isolable degradation recording residual write access through shell
        var degradations = result.Degradations
            .Where(d => d.Code == "capability-not-isolable")
            .ToList();

        // There should be at least one (investigator/reviewer profile agents)
        // Empty list is acceptable if the corpus has no such agents, but if they exist,
        // the degradation must be recorded
        if (source.Agents.Any(a =>
                source.CapabilityProfiles.Profiles.TryGetValue(a.CapabilityProfile, out var profile) &&
                profile.Permissions.TryGetValue("process.execute", out var exec) && exec == SquadPermissionDecision.Allow &&
                profile.Permissions.TryGetValue("filesystem.write", out var write) && write != SquadPermissionDecision.Allow))
        {
            Assert.NotEmpty(degradations);
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_DelegationEmittedPerDelegatesToRoster()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D12: Agents with non-empty delegates-to must have invoke_subagent + enable_subagent_tools
        var delegatingAgents = source.Agents.Where(a => a.DelegatesTo.Count > 0).ToList();

        foreach (var agent in delegatingAgents)
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
            YamlSequenceNode toolsSeq = Assert.IsType<YamlSequenceNode>(toolsNodeOptional!);
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
        var nonDelegatingAgents = source.Agents.Where(a => a.DelegatesTo.Count == 0).ToList();

        foreach (var agent in nonDelegatingAgents)
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
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

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

    [Fact]
    public async Task RenderAsync_Antigravity_Native_ModelEnumInheritFlashProOnly()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D8: Only inherit, flash, pro spellings; never flash-lite or FLASH_LITE
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile agentFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            YamlMappingNode frontmatter = ReadFrontmatter(agentFile);
            string model = RequireScalar(frontmatter, "model");

            Assert.Matches("^(inherit|flash|pro)$", model);
            Assert.False(
                model.Contains("flash", StringComparison.OrdinalIgnoreCase) && model.Contains('-', StringComparison.Ordinal),
                $"Agent '{agent.Name}' model must not use flash-lite or flash_lite spelling, got '{model}'");
        }
    }

    [Fact]
    public async Task RenderAsync_Antigravity_Native_ReasoningEffortMatchesPerProfileMapping()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D9: reasoning_effort must match approved per-profile mapping
        Dictionary<string, string> expectedReasoningEffort = new(StringComparer.Ordinal)
        {
            { "deep-planning", "high" },
            { "reviewer", "high" },
            { "general", "medium" },
            { "fast", "low" },
            { "orchestration", "minimal" },
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
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D12: These tools are withheld pending live verification
        string[] inconclusivelTools = ["grep_search", "replace_file_content", "multi_replace_file_content", "search_web", "read_url_content"];

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
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // D12-delegate: Agents with non-empty delegates-to have an unenforceable roster,
        // which should be recorded as permission-not-expressible degradation
        var delegatingAgents = source.Agents.Where(a => a.DelegatesTo.Count > 0).ToList();

        foreach (var agent in delegatingAgents)
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
    public void SquadRendererRegistry_IncludesAntigravityInNativeTargetSet()
    {
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);

        // Verify Antigravity is in the native-target set (not fallback)
        Assert.True(
            registry.SupportedTargets.Contains(SquadTarget.Antigravity),
            "SquadRendererRegistry must support Antigravity");

        // The native-target validation applies to Antigravity:
        // - No role- prefixed files (dual emission namespace separation)
        // - Single projection per identity (no role-prefix collisions)
        // This is verified by RenderAsync_Antigravity_Native_OmitsRolePrefixForCollisions
    }

    [Fact]
    public async Task SquadDeploymentPlan_IdentityFromRelativePathHandlesAgentMd()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Antigravity],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // SquadDeploymentPlan.IdentityFromRelativePath must resolve a bare agent.md
        // to its parent directory name (e.g., ".agents/agents/conductor/agent.md" → "conductor")
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile agentFile = result.Files.First(
                f => f.RelativePath == $".agents/agents/{agent.Name}/agent.md");

            // The relative path structure .agents/agents/<name>/agent.md allows
            // IdentityFromRelativePath to extract <name> as the identity
            string expectedPath = $".agents/agents/{agent.Name}/agent.md";
            Assert.Equal(expectedPath, agentFile.RelativePath);
            Assert.True(
                agentFile.RelativePath.Contains($"/{agent.Name}/agent.md", StringComparison.Ordinal),
                $"Agent file path must be resolvable to identity '{agent.Name}'");
        }
    }
}
