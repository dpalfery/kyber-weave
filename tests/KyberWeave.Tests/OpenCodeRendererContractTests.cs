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
    /// OpenCode's built-in tool vocabulary, grounded in OpenCode runtime telemetry
    /// from <c>dash/src/providers/opencode.ts</c>. A tool outside this vocabulary
    /// would be unhandled by the harness. <c>task(roster)</c> forms are validated
    /// separately because the roster is agent-specific.
    /// </summary>
    private static readonly string[] DocumentedOpenCodeTools =
    [
        "todo",
        "skill",
        "read",
        "grep",
        "glob",
        "edit",
        "write",
        "patch",
        "bash",
        "fetch",
        "search",
        "task"
    ];

    /// <summary>
    /// Capability-to-tool lowering contract pinned for OpenCode subagents.
    /// Declared independently of the renderer so a change to either side must be made
    /// deliberately in both. <c>network.publish</c> is absent because OpenCode has no built-in
    /// publish tool (recorded as <c>permission-not-expressible</c>). <c>delegate</c> is handled
    /// separately so non-empty <see cref="SquadAgent.DelegatesTo"/> emits <c>task(roster)</c>.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("filesystem.read", ["read"]),
        ("filesystem.search", ["grep", "glob"]),
        ("filesystem.write", ["edit", "write", "patch"]),
        ("process.execute", ["bash"]),
        ("network.read", ["fetch", "search"])
    ];

    /// <summary>
    /// Emission order of tools in OpenCode agent frontmatter, strictly deterministic.
    /// </summary>
    private static readonly string[] ToolOrder =
    [
        "todo",
        "skill",
        "read",
        "grep",
        "glob",
        "edit",
        "write",
        "patch",
        "bash",
        "fetch",
        "search",
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

    public void Dispose()
    {
        // No disposable state: suite reads canonical checked-in corpus.
        // Implements IDisposable per repository test coding standard.
        GC.SuppressFinalize(this);
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

            // Tools allowlist: omitting tools key inherits ambient access (silent widening),
            // so every OpenCode agent must carry an explicit tools allowlist.
            SquadCapabilityProfile capProfile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            IReadOnlyList<string> tools = RequireSequence(frontmatter, "tools", agent.Name);

            Assert.All(tools, tool => Assert.True(
                IsOpenCodeTool(tool),
                $"Agent '{agent.Name}' tool '{tool}' is not in the documented OpenCode vocabulary."));

            Assert.True(
                tools.Distinct(StringComparer.Ordinal).Count() == tools.Count,
                $"Agent '{agent.Name}' has duplicate tool entries: {string.Join(", ", tools)}.");

            // Ungoverned base tools
            Assert.Contains("todo", tools);
            Assert.Contains("skill", tools);

            // Governed capabilities lowering
            foreach ((string capability, string[] mapped) in CapabilityToolContract)
            {
                bool allowed = capProfile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                               decision == SquadPermissionDecision.Allow;
                foreach (string tool in mapped)
                {
                    Assert.True(
                        allowed == tools.Contains(tool, StringComparer.Ordinal),
                        $"Agent '{agent.Name}' capability '{capability}' allowed={allowed} tool '{tool}'.");
                }
            }

            // Delegation lowering
            bool delegateAllowed = capProfile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                                   delegateDecision == SquadPermissionDecision.Allow;
            if (delegateAllowed)
            {
                if (agent.DelegatesTo.Count > 0)
                {
                    string expectedTaskTool = $"task({string.Join(", ", agent.DelegatesTo)})";
                    Assert.Contains(
                        tools,
                        tool => string.Equals(tool, expectedTaskTool, StringComparison.Ordinal));
                }
                else
                {
                    Assert.Contains("task", tools);
                }
            }
            else
            {
                Assert.DoesNotContain(tools, tool => tool.StartsWith("task", StringComparison.Ordinal));
            }

            // Ordering: tools list must be deterministically ordered
            int lastOrderIndex = -1;
            foreach (string tool in tools)
            {
                string toolKey = tool.StartsWith("task(", StringComparison.Ordinal) ? "task" : tool;
                int orderIndex = Array.IndexOf(ToolOrder, toolKey);
                Assert.True(orderIndex >= 0, $"Agent '{agent.Name}' tool '{tool}' not found in ToolOrder.");
                Assert.True(orderIndex > lastOrderIndex, $"Agent '{agent.Name}' tool '{tool}' is out of order.");
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
        Assert.Equal(SquadPermissionDecision.Ask, architectProfile.Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Ask, architectProfile.Permissions["process.execute"]);
        AssertTools(result, "architect", ["todo", "skill", "read", "grep", "glob", "fetch", "search", "task(azure-reader, research-agent)"]);

        SquadCapabilityProfile docProfile = source.CapabilityProfiles.Profiles["documentation"];
        Assert.Equal(SquadPermissionDecision.Allow, docProfile.Permissions["filesystem.write"]);
        AssertTools(result, "docs-dev", ["todo", "skill", "read", "grep", "glob", "edit", "write", "patch"]);

        SquadCapabilityProfile investigatorProfile = source.CapabilityProfiles.Profiles["investigator"];
        Assert.Equal(SquadPermissionDecision.Allow, investigatorProfile.Permissions["process.execute"]);
        AssertTools(result, "bug-crusher-investigator", ["todo", "skill", "read", "grep", "glob", "bash", "fetch", "search"]);

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
                degradation.Code is "safety-narrowed" or "permission-not-expressible",
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

    private static void AssertTools(
        SquadRenderResult result,
        string agentName,
        IReadOnlyList<string> expectedTools)
    {
        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".opencode/agents/{agentName}.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(file.Content.Span),
            agentName);

        IReadOnlyList<string> actualTools = RequireSequence(frontmatter, "tools", agentName);
        Assert.Equal(expectedTools, actualTools);
    }

    private static bool IsOpenCodeTool(string tool) =>
        DocumentedOpenCodeTools.Contains(tool, StringComparer.Ordinal) ||
        (tool.StartsWith("task(", StringComparison.Ordinal) && tool.EndsWith(')'));

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
