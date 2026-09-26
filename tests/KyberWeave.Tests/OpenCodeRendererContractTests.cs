using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="OpenCodeRenderer"/> and validates the result
/// against OpenCode's documented subagent and skill contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents lower to OpenCode subagents at <c>.opencode/agents/&lt;name&gt;.md</c>
/// and skills lower to <c>.opencode/skills/&lt;name&gt;/SKILL.md</c>. Verifies profile-declared
/// shared-identity suppression, explicit tool allowlist lowering to OpenCode's tool taxonomy,
/// model resolution from <c>models.yml</c>, deterministic output, and structured degradation accounting
/// for <c>safety-narrowed</c> and <c>permission-not-expressible</c> codes.
/// </remarks>
public sealed class OpenCodeRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// OpenCode's built-in permission vocabulary, grounded in OpenCode runtime agent
    /// configurations and schema validation. A permission outside this vocabulary would be
    /// rejected or unhandled by the harness.
    /// </summary>
    /// <remarks>
    /// <c>question</c>, <c>lsp</c>, <c>external_directory</c> and <c>doom_loop</c> were added
    /// 2026-09-21 from opencode.ai/docs/permissions' "Available Permissions" list, corroborated
    /// by a live OpenCode deployment on this machine that emitted all four. They matter because
    /// an omitted key is not a withheld one: agent permissions merge with the global config,
    /// where most permissions default to <c>allow</c>, so any key the renderer fails to pin is
    /// granted ambiently.
    /// </remarks>
    private static readonly string[] DocumentedOpenCodePermissions =
    [
        "todowrite",
        "skill",
        "question",
        "read",
        "lsp",
        "grep",
        "glob",
        "list",
        "edit",
        "bash",
        "webfetch",
        "websearch",
        "external_directory",
        "doom_loop",
        "kyber-weave_*",
        "task"
    ];

    /// <summary>
    /// Capability-to-permission lowering contract pinned for OpenCode subagents.
    /// Declared independently of the renderer so a change to either side must be made
    /// deliberately in both. <c>network.publish</c> is absent because OpenCode has no built-in
    /// publish permission (recorded as <c>permission-not-expressible</c>). <c>delegate</c> is handled
    /// separately to emit pattern-based <c>task</c> rules.
    /// </summary>
    private static readonly (string Capability, string[] Permissions)[] CapabilityPermissionContract =
    [
        ("filesystem.read", ["read"]),
        ("filesystem.search", ["grep", "glob", "list"]),
        ("filesystem.write", ["edit"]),
        ("process.execute", ["bash"]),
        ("network.read", ["webfetch", "websearch"])
    ];

    /// <summary>
    /// Emission order of permissions in OpenCode agent frontmatter, strictly deterministic.
    /// </summary>
    private static readonly string[] PermissionOrder =
    [
        "todowrite",
        "skill",
        "question",
        "read",
        "lsp",
        "grep",
        "glob",
        "list",
        "edit",
        "bash",
        "webfetch",
        "websearch",
        "external_directory",
        "doom_loop",
        "kyber-weave_*",
        "task"
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
    public void SupportedTargets_IsExactlyOpenCode()
    {
        SquadRendererRegistry registry = new([new OpenCodeRenderer()]);

        Assert.Equal([SquadTarget.OpenCode], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new OpenCodeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Claude, SquadTarget.OpenCode],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("claude", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonOpenCodeTarget()
    {
        OpenCodeRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(request));
    }

    [Fact]
    public async Task RenderAsync_OpenCode_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> sharedIdentities = SharedIdentities(source);
        SquadRendererRegistry registry = new([new OpenCodeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.OpenCode],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // 1. Target filtering: requesting OpenCode renders only files targeting opencode
        Assert.All(result.Files, f => Assert.Equal("opencode", f.Target));
        Assert.All(result.Files, f => Assert.True(
            f.RelativePath.StartsWith(".opencode/agents/", StringComparison.Ordinal) ||
            f.RelativePath.StartsWith(".opencode/skills/", StringComparison.Ordinal),
            $"File '{f.RelativePath}' does not start with expected OpenCode directory."));

        // 2. Output counts: exactly 45 principal files (21 agents under .opencode/agents/*.md
        // + 24 skills under .opencode/skills/*/SKILL.md) plus linked resource closures.
        int principalAgentCount = result.Files.Count(f =>
            f.RelativePath.StartsWith(".opencode/agents/", StringComparison.Ordinal) &&
            f.RelativePath.EndsWith(".md", StringComparison.Ordinal) &&
            !f.RelativePath[".opencode/agents/".Length..^".md".Length].Contains('/', StringComparison.Ordinal));

        int principalSkillCount = result.Files.Count(f =>
            f.RelativePath.StartsWith(".opencode/skills/", StringComparison.Ordinal) &&
            f.RelativePath.EndsWith("/SKILL.md", StringComparison.Ordinal) &&
            !f.RelativePath[".opencode/skills/".Length..^"/SKILL.md".Length].Contains('/', StringComparison.Ordinal));

        Assert.Equal(21, principalAgentCount);
        Assert.Equal(24, principalSkillCount);
        Assert.Equal(45, principalAgentCount + principalSkillCount);

        int suppressedSkillCount = source.Skills.Count(skill => sharedIdentities.Contains(skill.Name));
        int expectedFileCount =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);

        Assert.Equal(expectedFileCount, result.Files.Count);
        Assert.Equal(113, result.Files.Count);

        // 3. Native single-projection rule: shared identities (like conductor) must NOT emit a skill
        // under .opencode/skills/conductor/SKILL.md, and no role- prefixes are emitted.
        Assert.Contains(result.Files, f => f.RelativePath == ".opencode/agents/conductor.md");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ".opencode/skills/conductor/SKILL.md");

        foreach (string sharedIdentity in sharedIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".opencode/agents/{sharedIdentity}.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".opencode/skills/{sharedIdentity}/SKILL.md");
        }

        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase));

        // 4. Agent frontmatter: verify YAML frontmatter contains name, description, optional model
        // resolved from models.yml, and explicit tools allowlist / permissions.
        Dictionary<string, SquadAgent> agentsByName = source.Agents.ToDictionary(a => a.Name, StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".opencode/agents/{agent.Name}.md");

            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(
                Encoding.UTF8.GetString(file.Content.Span),
                agent.Name);

            Assert.Equal(agent.Name, RequireScalar(frontmatter, "name", agent.Name));
            Assert.True(
                string.Equals(agent.Description, RequireScalar(frontmatter, "description", agent.Name), StringComparison.Ordinal),
                $"Agent '{agent.Name}' description mismatch.");

            string expectedMode = agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent";
            Assert.Equal(expectedMode, RequireScalar(frontmatter, "mode", agent.Name));

            // Model resolution: verify against loaded ModelProfiles for target opencode
            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            if (modelProfile.HarnessModels.TryGetValue("opencode", out string? opencodeHarnessModel))
            {
                if (string.Equals(opencodeHarnessModel, "inherit", StringComparison.Ordinal))
                {
                    Assert.False(
                        frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                        $"Agent '{agent.Name}' should omit 'model' (opencode harness override is inherit).");
                }
                else
                {
                    Assert.True(
                        string.Equals(opencodeHarnessModel, RequireScalar(frontmatter, "model", agent.Name), StringComparison.Ordinal),
                        $"Agent '{agent.Name}' model mismatch: expected '{opencodeHarnessModel}'.");
                }
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal))
            {
                Assert.True(
                    string.Equals(modelProfile.Default, RequireScalar(frontmatter, "model", agent.Name), StringComparison.Ordinal),
                    $"Agent '{agent.Name}' model mismatch: expected '{modelProfile.Default}'.");
            }
            else
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' should omit 'model' (profile default is inherit).");
            }

            // OpenCode agents emit 'permission' map instead of 'tools' sequence
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("tools")),
                $"Agent '{agent.Name}' should not emit 'tools'.");

            SquadCapabilityProfile capProfile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            YamlMappingNode permissionMap = RequireMapping(frontmatter, "permission", agent.Name);

            // Verify all permission keys belong to documented vocabulary
            foreach (YamlNode keyNode in permissionMap.Children.Keys)
            {
                string permKey = Assert.IsType<YamlScalarNode>(keyNode).Value!;
                Assert.True(
                    IsOpenCodePermission(permKey),
                    $"Agent '{agent.Name}' permission '{permKey}' is not in the documented OpenCode vocabulary.");
            }

            // Ungoverned base permissions
            Assert.Equal("allow", RequireScalar(permissionMap, "todowrite", agent.Name));
            Assert.Equal("allow", RequireScalar(permissionMap, "skill", agent.Name));

            // Governed capabilities lowering
            foreach ((string capability, string[] mapped) in CapabilityPermissionContract)
            {
                bool allowed = capProfile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                               decision == SquadPermissionDecision.Allow;
                foreach (string perm in mapped)
                {
                    // Presence is not the signal: an absent key inherits the global default,
                    // which is allow. Every key must be pinned, and the value carries the
                    // decision.
                    Assert.True(
                        permissionMap.Children.ContainsKey(new YamlScalarNode(perm)),
                        $"Agent '{agent.Name}' leaves permission '{perm}' unpinned, so OpenCode's " +
                        "default-allow would grant it.");
                    Assert.Equal(
                        allowed ? "allow" : "deny",
                        RequireScalar(permissionMap, perm, agent.Name));
                }
            }

            // D8 MCP mapping: kyber-weave_* granted to agents with filesystem.read: allow, excluding pure orchestrators and shared identities
            bool isPureOrchestrator = string.Equals(agent.CapabilityProfile, "orchestrator", StringComparison.Ordinal);
            bool readAllowed = capProfile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
                               readDecision == SquadPermissionDecision.Allow;
            bool expectedKwMcp = !isPureOrchestrator && !sharedIdentities.Contains(agent.Name) && readAllowed;
            Assert.True(
                permissionMap.Children.ContainsKey(new YamlScalarNode("kyber-weave_*")),
                $"Agent '{agent.Name}' leaves 'kyber-weave_*' unpinned.");
            Assert.Equal(
                expectedKwMcp ? "allow" : "deny",
                RequireScalar(permissionMap, "kyber-weave_*", agent.Name));

            // Delegation lowering
            bool delegateAllowed = capProfile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                                   delegateDecision == SquadPermissionDecision.Allow;
            if (delegateAllowed)
            {
                Assert.True(
                    permissionMap.Children.TryGetValue(new YamlScalarNode("task"), out YamlNode? taskNode),
                    $"Agent '{agent.Name}' missing 'task' permission.");
                if (agent.DelegatesTo.Count > 0)
                {
                    YamlMappingNode taskMap = Assert.IsType<YamlMappingNode>(taskNode);
                    string[] sortedDelegates = agent.DelegatesTo.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    string[] taskKeys = taskMap.Children.Keys
                        .OfType<YamlScalarNode>()
                        .Select(k => k.Value!)
                        .ToArray();
                    Assert.Equal(sortedDelegates, taskKeys);
                    foreach (string target in sortedDelegates)
                    {
                        Assert.Equal("allow", RequireScalar(taskMap, target, agent.Name));
                    }
                }
                else
                {
                    YamlScalarNode taskScalar = Assert.IsType<YamlScalarNode>(taskNode);
                    Assert.Equal("allow", taskScalar.Value);
                }
            }
            else
            {
                Assert.Equal("deny", RequireScalar(permissionMap, "task", agent.Name));
            }

            // Ordering: permission map keys must be deterministically ordered
            int lastOrderIndex = -1;
            foreach (YamlNode keyNode in permissionMap.Children.Keys)
            {
                string permKey = Assert.IsType<YamlScalarNode>(keyNode).Value!;
                int orderIndex = Array.IndexOf(PermissionOrder, permKey);
                Assert.True(orderIndex >= 0, $"Agent '{agent.Name}' permission '{permKey}' not found in PermissionOrder.");
                Assert.True(orderIndex > lastOrderIndex, $"Agent '{agent.Name}' permission '{permKey}' is out of order.");
                lastOrderIndex = orderIndex;
            }

            // Body verification
            string expectedAgentBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!expectedAgentBody.EndsWith('\n'))
            {
                expectedAgentBody += "\n";
            }

            Assert.True(
                string.Equals(expectedAgentBody, body, StringComparison.Ordinal),
                $"Agent '{agent.Name}' body mismatch.");
        }

        // Concrete lowering snapshots
        SquadCapabilityProfile architectProfile = source.CapabilityProfiles.Profiles["architect"];
        Assert.Equal(SquadPermissionDecision.Allow, architectProfile.Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Ask, architectProfile.Permissions["process.execute"]);
        AssertPermissions(
            result,
            "architect",
            new Dictionary<string, object>
            {
                ["todowrite"] = "allow",
                ["skill"] = "allow",
                ["question"] = "allow",
                ["read"] = "allow",
                ["lsp"] = "allow",
                ["grep"] = "allow",
                ["glob"] = "allow",
                ["list"] = "allow",
                ["edit"] = "allow",
                ["bash"] = "deny",
                ["webfetch"] = "allow",
                ["websearch"] = "allow",
                ["external_directory"] = "deny",
                ["doom_loop"] = "deny",
                ["kyber-weave_*"] = "allow",
                ["task"] = new Dictionary<string, string>
                {
                    ["azure-reader"] = "allow",
                    ["research-agent"] = "allow",
                }
            });

        SquadCapabilityProfile docProfile = source.CapabilityProfiles.Profiles["documentation"];
        Assert.Equal(SquadPermissionDecision.Allow, docProfile.Permissions["filesystem.write"]);
        AssertPermissions(
            result,
            "docs-dev",
            new Dictionary<string, object>
            {
                ["todowrite"] = "allow",
                ["skill"] = "allow",
                ["question"] = "allow",
                ["read"] = "allow",
                ["lsp"] = "allow",
                ["grep"] = "allow",
                ["glob"] = "allow",
                ["list"] = "allow",
                ["edit"] = "allow",
                ["bash"] = "deny",
                ["webfetch"] = "deny",
                ["websearch"] = "deny",
                ["external_directory"] = "deny",
                ["doom_loop"] = "deny",
                ["kyber-weave_*"] = "allow",
                ["task"] = "deny"
            });

        SquadCapabilityProfile investigatorProfile = source.CapabilityProfiles.Profiles["investigator"];
        Assert.Equal(SquadPermissionDecision.Allow, investigatorProfile.Permissions["process.execute"]);
        AssertPermissions(
            result,
            "bug-crusher-investigator",
            new Dictionary<string, object>
            {
                ["todowrite"] = "allow",
                ["skill"] = "allow",
                ["question"] = "allow",
                ["read"] = "allow",
                ["lsp"] = "allow",
                ["grep"] = "allow",
                ["glob"] = "allow",
                ["list"] = "allow",
                ["edit"] = "deny",
                ["bash"] = "allow",
                ["webfetch"] = "allow",
                ["websearch"] = "allow",
                ["external_directory"] = "deny",
                ["doom_loop"] = "deny",
                ["kyber-weave_*"] = "allow",
                ["task"] = "deny"
            });

        // Skills verification
        foreach (SquadSkill skill in source.Skills)
        {
            bool isSharedIdentity = sharedIdentities.Contains(skill.Name);
            string path = $".opencode/skills/{skill.Name}/SKILL.md";
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

        // 5. Degradation records: verify agents with network.publish != Deny (e.g., github-devops) emit
        // a SquadDegradationRecord with code permission-not-expressible and InstructionDigest matching agent.BodyDigest
        string[] expectedPublishDegraded = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                return prof.Permissions.TryGetValue("network.publish", out SquadPermissionDecision decision) &&
                       decision != SquadPermissionDecision.Deny;
            })
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedPublishDegraded);
        Assert.Contains("github-devops", expectedPublishDegraded);

        foreach (string agentName in expectedPublishDegraded)
        {
            SquadDegradationRecord degradation = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == agentName &&
                     d.Code == "permission-not-expressible" &&
                     d.Details != null && d.Details.Contains("network.publish", StringComparison.Ordinal));

            Assert.Equal("opencode", degradation.Target);
            Assert.Equal(agentName, degradation.OutputIdentity);
            SquadAgent agent = agentsByName[agentName];
            Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
        }

        // Safety-narrowed: 'ask' decisions cannot be confirmed per-capability in OpenCode subagents,
        // so they narrow to withhold the tool and emit safety-narrowed.
        string[] expectedSafetyNarrowed = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                return prof.Permissions.Values.Any(decision => decision == SquadPermissionDecision.Ask);
            })
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expectedSafetyNarrowed);
        Assert.Contains("architect", expectedSafetyNarrowed);

        IReadOnlyList<SquadDegradationRecord> safetyNarrowed = result.Degradations
            .Where(d => string.Equals(d.Code, "safety-narrowed", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            expectedSafetyNarrowed,
            safetyNarrowed.Select(d => d.CanonicalIdentity).OrderBy(n => n, StringComparer.Ordinal));

        // Permission-not-expressible: network.publish != Deny and delegate-allow with non-empty DelegatesTo roster
        string[] expectedPermissionNotExpressible = source.Agents
            .Where(a =>
            {
                SquadCapabilityProfile prof = source.CapabilityProfiles.Profiles[a.CapabilityProfile];
                bool publishNotDeny = prof.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
                                      publishDecision != SquadPermissionDecision.Deny;
                bool rosterLimited = prof.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                                     delegateDecision == SquadPermissionDecision.Allow &&
                                     a.DelegatesTo.Count > 0;
                return publishNotDeny || rosterLimited;
            })
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        IReadOnlyList<SquadDegradationRecord> permissionNotExpressible = result.Degradations
            .Where(d => string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            expectedPermissionNotExpressible,
            permissionNotExpressible.Select(d => d.CanonicalIdentity).OrderBy(n => n, StringComparer.Ordinal));

        // Digest integrity: Every degradation record must match target, output identity, and agent InstructionDigest
        foreach (SquadDegradationRecord degradation in result.Degradations)
        {
            Assert.Equal("opencode", degradation.Target);
            Assert.True(
                degradation.Code is "safety-narrowed" or "permission-not-expressible" or "capability-not-isolable",
                $"Degradation for '{degradation.CanonicalIdentity}' has unexpected code '{degradation.Code}'.");
            Assert.Equal(degradation.CanonicalIdentity, degradation.OutputIdentity);
            SquadAgent agent = agentsByName[degradation.CanonicalIdentity];
            Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
        }

        // Anti-widening check
        Assert.DoesNotContain(
            result.Degradations,
            d => d.Code.Contains("widen", StringComparison.OrdinalIgnoreCase) ||
                 (d.Details is not null && d.Details.Contains("widening", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Agents with process.execute: allow and filesystem.write: ask or deny (e.g. investigator and
    /// reviewer profiles) receive a capability-not-isolable degradation record naming the granted
    /// shell tools (bash) and withheld write tools (edit).
    /// Agents with filesystem.write: allow or process.execute: deny do not receive this degradation.
    /// </summary>
    [Fact]
    public async Task RenderAsync_OpenCode_RecordsCapabilityNotIsolableForShellImpliesWrite()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new OpenCodeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.OpenCode],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        string[] grantedShellTools = ["bash"];
        string[] withheldWriteTools = ["edit"];

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
                Assert.Equal("opencode", record.Target);
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
    public async Task RenderAsync_IsDeterministic()
    {
        SquadRendererRegistry registry = new([new OpenCodeRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.OpenCode],
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
            Assert.True(
                file1.Content.Span.SequenceEqual(file2.Content.Span),
                $"Content differed for file '{file1.RelativePath}'.");
        }

        Assert.Equal(first.Degradations.Count, second.Degradations.Count);
        for (int i = 0; i < first.Degradations.Count; i++)
        {
            SquadDegradationRecord d1 = first.Degradations[i];
            SquadDegradationRecord d2 = second.Degradations[i];

            Assert.Equal(d1.Target, d2.Target);
            Assert.Equal(d1.CanonicalIdentity, d2.CanonicalIdentity);
            Assert.Equal(d1.OutputIdentity, d2.OutputIdentity);
            Assert.Equal(d1.Code, d2.Code);
            Assert.Equal(d1.InstructionDigest, d2.InstructionDigest);
            Assert.Equal(d1.Details, d2.Details);
        }
    }

    [Fact]
    public async Task RenderAsync_OpenCode_ProjectsLinkedAgentAndSkillResourcesDeterministically()
    {
        await SquadResourceRenderingContract.AssertNativeProjectionAsync(
            new OpenCodeRenderer(),
            SquadTarget.OpenCode,
            ".opencode/agents/bug-crusher-investigator.md",
            ".opencode/agents",
            ".opencode/skills");
    }

    [Fact]
    public async Task RenderAsync_NativeSingleProjectionRule_SharedIdentityEmitsAgentAndSuppressesSkill()
    {
        using SharedIdentitySquadFixture fixture = SharedIdentitySquadFixture.Create();
        SquadRendererRegistry registry = new([new OpenCodeRenderer()]);
        SquadRenderRequest request = new(
            fixture.Path,
            [SquadTarget.OpenCode],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(
            result.Files,
            file => file.RelativePath == $".opencode/agents/{SharedIdentitySquadFixture.Identity}.md");
        Assert.DoesNotContain(
            result.Files,
            file => file.RelativePath == $".opencode/skills/{SharedIdentitySquadFixture.Identity}/SKILL.md");
    }

    private static void AssertPermissions(
        SquadRenderResult result,
        string agentName,
        IReadOnlyDictionary<string, object> expectedPermissions)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".opencode/agents/{agentName}.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(file.Content.Span),
            agentName);

        YamlMappingNode actualPermissions = RequireMapping(frontmatter, "permission", agentName);

        string[] expectedKeys = expectedPermissions.Keys.ToArray();
        string[] actualKeys = actualPermissions.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(k => k.Value!)
            .ToArray();
        Assert.Equal(expectedKeys, actualKeys);

        foreach ((string key, object expectedVal) in expectedPermissions)
        {
            YamlNode actualValNode = actualPermissions.Children[new YamlScalarNode(key)];
            if (expectedVal is string expectedScalar)
            {
                YamlScalarNode actualScalar = Assert.IsType<YamlScalarNode>(actualValNode);
                Assert.Equal(expectedScalar, actualScalar.Value);
            }
            else if (expectedVal is IReadOnlyDictionary<string, string> expectedMap)
            {
                YamlMappingNode actualMap = Assert.IsType<YamlMappingNode>(actualValNode);
                string[] expectedSubKeys = expectedMap.Keys.ToArray();
                string[] actualSubKeys = actualMap.Children.Keys
                    .OfType<YamlScalarNode>()
                    .Select(k => k.Value!)
                    .ToArray();
                Assert.Equal(expectedSubKeys, actualSubKeys);
                foreach ((string subKey, string expectedSubVal) in expectedMap)
                {
                    Assert.Equal(expectedSubVal, RequireScalar(actualMap, subKey, agentName));
                }
            }
            else
            {
                throw new InvalidOperationException($"Unexpected expectedVal type {expectedVal?.GetType()}");
            }
        }
    }

    private static bool IsOpenCodePermission(string permission) =>
        DocumentedOpenCodePermissions.Contains(permission, StringComparer.Ordinal);

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

    private static YamlMappingNode RequireMapping(YamlMappingNode node, string key, string identity)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value))
        {
            string presentKeys = string.Join(", ", node.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(existing => existing.Value ?? "<null>"));
            throw new InvalidOperationException(
                $"'{identity}' frontmatter is missing required key '{key}'. Present keys: {presentKeys}.");
        }

        return Assert.IsType<YamlMappingNode>(value);
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
