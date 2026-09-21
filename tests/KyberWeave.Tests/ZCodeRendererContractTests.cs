using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="ZCodeRenderer"/> and validates the
/// result against the ZCode rendering contract
/// (<c>docs/plans/2026-09-21-zcode-harness-target.md</c>, section 3).
/// </summary>
/// <remarks>
/// <para>
/// ZCode is a native target with three primitives: the 20 subagent-invocation agents render
/// as <c>.zcode/agents/&lt;name&gt;.md</c>, canonical skills as
/// <c>.zcode/skills/&lt;name&gt;/SKILL.md</c>, and the one <c>invocation: primary</c> agent
/// (<c>conductor</c>) lowers to a slash command at <c>.zcode/commands/&lt;name&gt;.md</c>,
/// because ZCode's agent modes are a closed set of permission modes rather than selectable
/// agents.
/// </para>
/// <para>
/// Every fact pinned here was verified on 2026-09-21 against <c>zai-org/ZCode</c> at version
/// <b>3.14.0</b> (Apache-2.0) — the source, not the published documentation, which still
/// states that project-level subagents do not exist while
/// <c>bootstrap/src/subagents.ts</c> loads them. The frontmatter key sets come from
/// <c>core/src/subagent/profile.ts</c>, <c>adapters/src/skills/index.ts</c>, and
/// <c>adapters/src/commands/index.ts</c>; the recursive agent and command scans from
/// <c>listMarkdownFiles</c> and <c>scanMarkdownFiles</c>; the tool vocabulary from
/// <c>core/src/tool/provider-visible-order.ts</c>; and the scalar form from
/// <c>services/src/subagents/subagentMarkdown.ts</c>.
/// </para>
/// </remarks>
public sealed class ZCodeRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// The capability→tool lowering this suite pins, transcribed independently from plan
    /// section 3 (D4) rather than read from the renderer. <c>network.publish</c> is absent
    /// because no built-in ZCode tool expresses it.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityToolContract =
    [
        ("filesystem.read", ["Read"]),
        ("filesystem.search", ["Grep", "Glob"]),
        ("filesystem.write", ["Edit", "Write"]),
        ("process.execute", ["Bash"]),
        ("network.read", ["WebFetch", "WebSearch"]),
        ("delegate", ["Agent"]),
    ];

    /// <summary>Fixed emission order for the tool list, per plan section 3.</summary>
    private static readonly string[] ToolOrder =
        ["Read", "Grep", "Glob", "Edit", "Write", "Bash", "WebFetch", "WebSearch", "Agent", "TodoWrite"];

    /// <summary>
    /// <c>SAFE_FRONTMATTER_KEYS</c> for a skill (<c>adapters/src/skills/index.ts</c>). A key
    /// outside this set clears <c>safeToAutoLoad</c> and disables implicit invocation, so the
    /// renderer must stay inside it.
    /// </summary>
    private static readonly HashSet<string> SkillSafeFrontmatterKeys =
        new(StringComparer.Ordinal) { "name", "description", "when_to_use", "license", "metadata" };

    /// <summary>
    /// <c>SAFE_FRONTMATTER_KEYS</c> for a command — kebab-case, unlike an agent's camelCase.
    /// A key outside this set raises a <c>custom_command_unknown_frontmatter</c> warning.
    /// </summary>
    private static readonly HashSet<string> CommandSafeFrontmatterKeys =
        new(StringComparer.Ordinal)
        { "allowed-tools", "argument-hint", "description", "disable-noninteractive", "model", "skills" };

    /// <summary>
    /// Accepted agent frontmatter keys (<c>core/src/subagent/profile.ts</c>).
    /// <c>permissionMode</c> is accepted by ZCode but deliberately never emitted, because
    /// <c>sanitizeProjectAgentProfile</c> strips it from project-scope profiles anyway.
    /// </summary>
    private static readonly HashSet<string> AgentAcceptedFrontmatterKeys =
        new(StringComparer.Ordinal)
        {
            "name", "description", "model", "thoughtLevel", "color", "tools", "disallowedTools",
            "skills", "permissionMode", "maxTurns", "background", "injectAgentsMd", "mcpServers"
        };

    /// <summary>ZCode's reserved built-in subagent names (<c>RESERVED_AGENT_NAMES</c>).</summary>
    private static readonly HashSet<string> ReservedAgentNames =
        new(StringComparer.Ordinal) { "general-purpose", "Explore" };

    /// <summary>
    /// ZCode drops a skill whose description exceeds this length outright rather than
    /// truncating it (<c>MAX_DESCRIPTION_LENGTH</c>, <c>skill_description_too_long</c>).
    /// </summary>
    private const int MaxDescriptionLength = 1024;

    private static HashSet<string> SharedIdentities(SquadSource source) =>
        source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> ComputeExpectedTools(SquadCapabilityProfile profile)
    {
        // TodoWrite is granted unconditionally: it writes no file and executes nothing, so it
        // cannot broaden any canonical decision.
        HashSet<string> granted = new(StringComparer.Ordinal) { "TodoWrite" };
        foreach ((string capability, string[] tools) in CapabilityToolContract)
        {
            if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision == SquadPermissionDecision.Allow)
            {
                foreach (string tool in tools)
                {
                    granted.Add(tool);
                }
            }
        }

        return ToolOrder.Where(granted.Contains).ToArray();
    }

    /// <summary>
    /// Parses the frontmatter the way ZCode does — a hand-rolled line reader, not a YAML
    /// parser — so this suite verifies what the harness will actually read rather than what a
    /// conforming YAML parser would make of the same bytes.
    /// </summary>
    private static (Dictionary<string, string> Scalars, Dictionary<string, List<string>> Lists) ParseFrontmatter(
        string document,
        string subject)
    {
        string[] lines = document.Split('\n');
        Assert.True(lines.Length > 0 && lines[0] == "---", $"{subject}: missing opening fence.");

        int end = Array.FindIndex(lines, 1, line => line == "---");
        Assert.True(end > 0, $"{subject}: missing closing fence.");

        Dictionary<string, string> scalars = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> lists = new(StringComparer.Ordinal);
        string? pendingListKey = null;

        for (int index = 1; index < end; index++)
        {
            string line = lines[index];
            if (line.StartsWith("  - ", StringComparison.Ordinal))
            {
                Assert.NotNull(pendingListKey);
                lists[pendingListKey!].Add(Unquote(line[4..]));
                continue;
            }

            int separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (line.EndsWith(':') && separator < 0)
            {
                pendingListKey = line[..^1];
                lists[pendingListKey] = [];
                continue;
            }

            Assert.True(separator > 0, $"{subject}: line '{line}' is not a ZCode-parseable key/value.");
            string key = line[..separator];
            string value = line[(separator + 2)..];
            pendingListKey = null;

            if (value == "[]")
            {
                lists[key] = [];
                continue;
            }

            scalars[key] = Unquote(value);
        }

        return (scalars, lists);
    }

    /// <summary>
    /// Mirrors ZCode's <c>unquoteScalar</c> plus the <c>\n</c> unescape its description path
    /// applies, so an assertion compares what the harness ends up holding.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1].Replace("\\n", "\n", StringComparison.Ordinal);
        }

        return value;
    }

    private static string DocumentText(SquadDeploymentFile file) =>
        Encoding.UTF8.GetString(file.Content.Span);

    private static string CollapseToSingleLine(string value) =>
        string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// The canonical description as ZCode will actually hold it after reading a rendered file.
    /// </summary>
    /// <remarks>
    /// ZCode's writer escapes <c>\</c> and <c>"</c> when it emits a double-quoted scalar
    /// (<c>escapeYamlString</c>), but neither of its readers unescapes them —
    /// <c>unquoteScalar</c> and the skill adapter's <c>parseScalar</c> both just slice the
    /// outer quote pair. A description containing either character therefore comes back with
    /// the backslash visible. That asymmetry is upstream's own round-trip behaviour for its
    /// own output, so this suite pins it rather than asserting a lossless round-trip the
    /// harness does not provide. See
    /// <c>docs/todo/zcode-skill-description-block-scalar.md</c> for the lossless alternative
    /// available to skills only.
    /// </remarks>
    private static string AsZCodeReadsIt(string canonical) =>
        CollapseToSingleLine(canonical)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static async Task<SquadRenderResult> RenderZCodeAsync(
        string sourceDirectory,
        SquadDeploymentScope scope = SquadDeploymentScope.Project)
    {
        SquadRendererRegistry registry = new([new ZCodeRenderer()]);
        return await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: sourceDirectory,
            Targets: [SquadTarget.ZCode],
            Scope: scope));
    }

    public void Dispose()
    {
        // No disposable state: the suite reads the checked-in corpus, plus per-test fixture
        // copies disposed locally via `using`. IDisposable is implemented per the
        // test-coding standard so a future class-level fixture has a home.
    }

    [Fact]
    public void SupportedTargets_IsExactlyZCode()
    {
        SquadRendererRegistry registry = new([new ZCodeRenderer()]);

        Assert.Equal([SquadTarget.ZCode], registry.SupportedTargets);
    }

    [Fact]
    public async Task RenderAsync_UnsupportedTarget_FailsBeforeAnyRendererRuns()
    {
        SquadRendererRegistry registry = new([new ZCodeRenderer()]);

        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.ZCode, SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Project));

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Contains(result.Errors, e => e.Contains("cursor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderAsync_Guard_RejectsNonZCodeTarget()
    {
        ZCodeRenderer renderer = new();

        await Assert.ThrowsAsync<ArgumentException>(() => renderer.RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project)));
    }

    [Fact]
    public async Task RenderAsync_ZCode_ProjectsEveryCanonicalPrincipalExactlyOnce()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> shared = SharedIdentities(source);

        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Every agent contributes exactly one principal — an agent file or, for the lowered
        // primary, a command — plus its resource closure under the skills tree. Every
        // non-suppressed skill contributes its SKILL.md plus its own closure beside it.
        int suppressedSkills = source.Skills.Count(skill => shared.Contains(skill.Name));
        int expected =
            source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + source.Skills.Count - suppressedSkills
            + source.Skills.Where(skill => !shared.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);

        Assert.Equal(expected, result.Files.Count);
        Assert.All(result.Files, file => Assert.Equal("zcode", file.Target));
        Assert.Equal(
            result.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count(),
            result.Files.Count);

        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent))
        {
            Assert.Single(result.Files, f => f.RelativePath == $".zcode/agents/{agent.Name}.md");
        }

        foreach (SquadSkill skill in source.Skills.Where(s => !shared.Contains(s.Name)))
        {
            Assert.Single(result.Files, f => f.RelativePath == $".zcode/skills/{skill.Name}/SKILL.md");
        }
    }

    [Fact]
    public async Task RenderAsync_ZCode_LowersThePrimaryAgentToASlashCommand()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent primary = Assert.Single(
            source.Agents,
            agent => agent.Invocation == SquadInvocation.Primary);

        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        SquadDeploymentFile command = Assert.Single(
            result.Files, f => f.RelativePath == $".zcode/commands/{primary.Name}.md");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == $".zcode/agents/{primary.Name}.md");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == $".zcode/skills/{primary.Name}/SKILL.md");

        (Dictionary<string, string> scalars, Dictionary<string, List<string>> lists) =
            ParseFrontmatter(DocumentText(command), primary.Name);

        // A command's name comes from its path, not its frontmatter, and 'name' is not a
        // SAFE_FRONTMATTER_KEY for commands — emitting it would raise an unknown-key warning.
        Assert.DoesNotContain("name", scalars.Keys, StringComparer.Ordinal);
        Assert.Equal(AsZCodeReadsIt(primary.Description), scalars["description"]);
        Assert.All(
            scalars.Keys.Concat(lists.Keys),
            key => Assert.Contains(key, CommandSafeFrontmatterKeys, StringComparer.Ordinal));

        // The tool list is 'allowed-tools' on a command, not 'tools'.
        Assert.Contains("allowed-tools", lists.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("tools", lists.Keys, StringComparer.Ordinal);
        Assert.Equal(
            ComputeExpectedTools(source.CapabilityProfiles.Profiles[primary.CapabilityProfile]),
            lists["allowed-tools"]);

        SquadDegradationRecord lowering = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == primary.Name && d.Code == "role-skill-fallback");
        Assert.Equal(primary.BodyDigest, lowering.InstructionDigest);
        Assert.Contains(".zcode/commands/", lowering.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_ZCode_EmitsOnlyAcceptedAgentFrontmatterKeys()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent))
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files, f => f.RelativePath == $".zcode/agents/{agent.Name}.md");
            (Dictionary<string, string> scalars, Dictionary<string, List<string>> lists) =
                ParseFrontmatter(DocumentText(file), agent.Name);

            Assert.All(
                scalars.Keys.Concat(lists.Keys),
                key => Assert.Contains(key, AgentAcceptedFrontmatterKeys, StringComparer.Ordinal));

            Assert.Equal(agent.Name, scalars["name"]);
            Assert.Equal(AsZCodeReadsIt(agent.Description), scalars["description"]);
            Assert.DoesNotContain(agent.Name, ReservedAgentNames, StringComparer.Ordinal);

            // permissionMode is stripped from project-scope profiles by ZCode itself; emitting
            // it would be a claim the harness silently discards.
            Assert.DoesNotContain("permissionMode", scalars.Keys, StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task RenderAsync_ZCode_AlwaysEmitsTheToolListBecauseOmittingItGrantsEverything()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent))
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files, f => f.RelativePath == $".zcode/agents/{agent.Name}.md");
            (_, Dictionary<string, List<string>> lists) = ParseFrontmatter(DocumentText(file), agent.Name);

            Assert.Contains("tools", lists.Keys, StringComparer.Ordinal);
            Assert.Equal(
                ComputeExpectedTools(source.CapabilityProfiles.Profiles[agent.CapabilityProfile]),
                lists["tools"]);
        }
    }

    /// <summary>
    /// The lowering may never widen: a tool appears only where the canonical decision is
    /// <c>allow</c>, because ZCode has no per-capability prompt and the list is binary.
    /// </summary>
    [Fact]
    public async Task RenderAsync_ZCode_AskAndDenyBothWithholdTheirTools()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        foreach (SquadAgent agent in source.Agents.Where(a => a.Invocation == SquadInvocation.Subagent))
        {
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            SquadDeploymentFile file = Assert.Single(
                result.Files, f => f.RelativePath == $".zcode/agents/{agent.Name}.md");
            (_, Dictionary<string, List<string>> lists) = ParseFrontmatter(DocumentText(file), agent.Name);
            HashSet<string> granted = lists["tools"].ToHashSet(StringComparer.Ordinal);

            foreach ((string capability, string[] tools) in CapabilityToolContract)
            {
                if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                    decision != SquadPermissionDecision.Allow)
                {
                    Assert.All(tools, tool => Assert.DoesNotContain(tool, granted, StringComparer.Ordinal));
                }
            }

            List<string> asked = profile.Permissions
                .Where(pair => pair.Value == SquadPermissionDecision.Ask)
                .Select(pair => pair.Key)
                .ToList();
            if (asked.Count > 0)
            {
                SquadDegradationRecord narrowed = Assert.Single(
                    result.Degradations,
                    d => d.CanonicalIdentity == agent.Name && d.Code == "safety-narrowed");
                Assert.Equal(agent.BodyDigest, narrowed.InstructionDigest);
                Assert.All(asked, capability =>
                    Assert.Contains(capability, narrowed.Details!, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// ZCode's <c>toolNameFromSpec</c> truncates every specifier at <c>(</c>, so an allowed
    /// <c>delegate</c> grants unscoped <c>Agent</c> and the roster must be recorded as
    /// unenforceable rather than claimed.
    /// </summary>
    [Fact]
    public async Task RenderAsync_ZCode_RecordsThatTheDelegationRosterIsNotEnforceable()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        List<SquadAgent> delegating = source.Agents
            .Where(agent => agent.Invocation == SquadInvocation.Subagent)
            .Where(agent =>
                source.CapabilityProfiles.Profiles[agent.CapabilityProfile].Permissions
                    .TryGetValue("delegate", out SquadPermissionDecision decision) &&
                decision == SquadPermissionDecision.Allow)
            .ToList();

        Assert.NotEmpty(delegating);

        foreach (SquadAgent agent in delegating)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files, f => f.RelativePath == $".zcode/agents/{agent.Name}.md");
            (_, Dictionary<string, List<string>> lists) = ParseFrontmatter(DocumentText(file), agent.Name);

            // Bare 'Agent' — never 'Agent(...)', which ZCode would store as bare 'Agent' anyway.
            Assert.Contains("Agent", lists["tools"], StringComparer.Ordinal);
            Assert.All(lists["tools"], tool => Assert.DoesNotContain('(', tool));

            SquadDegradationRecord record = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == agent.Name && d.Code == "permission-not-expressible");
            Assert.Equal(agent.BodyDigest, record.InstructionDigest);
            Assert.Contains("delegates-to", record.Details!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RenderAsync_ZCode_EmitsOnlySafeSkillFrontmatterKeysWithinTheDescriptionCap()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> shared = SharedIdentities(source);
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        foreach (SquadSkill skill in source.Skills.Where(s => !shared.Contains(s.Name)))
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files, f => f.RelativePath == $".zcode/skills/{skill.Name}/SKILL.md");
            (Dictionary<string, string> scalars, Dictionary<string, List<string>> lists) =
                ParseFrontmatter(DocumentText(file), skill.Name);

            Assert.All(
                scalars.Keys.Concat(lists.Keys),
                key => Assert.Contains(key, SkillSafeFrontmatterKeys, StringComparer.Ordinal));
            Assert.Equal(skill.Name, scalars["name"]);
            Assert.Equal(AsZCodeReadsIt(skill.Description), scalars["description"]);
            Assert.True(
                scalars["description"].Length <= MaxDescriptionLength,
                $"Skill '{skill.Name}' description is {scalars["description"].Length} characters; " +
                $"ZCode drops a skill over {MaxDescriptionLength}.");
        }
    }

    /// <summary>
    /// ZCode's frontmatter reader is a line parser, not a YAML parser: a value split across
    /// physical lines is silently dropped. This is the guard that keeps a future serializer
    /// change from folding a long description into something the harness cannot read.
    /// </summary>
    [Fact]
    public async Task RenderAsync_ZCode_EveryFrontmatterValueIsASinglePhysicalLine()
    {
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        foreach (SquadDeploymentFile file in result.Files.Where(IsPrincipal))
        {
            string[] lines = DocumentText(file).Split('\n');
            int end = Array.FindIndex(lines, 1, line => line == "---");
            Assert.True(end > 0, $"{file.RelativePath}: missing closing fence.");

            for (int index = 1; index < end; index++)
            {
                string line = lines[index];
                bool isKeyValue = line.Contains(": ", StringComparison.Ordinal) || line.EndsWith(':');
                bool isListItem = line.StartsWith("  - ", StringComparison.Ordinal);
                Assert.True(
                    isKeyValue || isListItem,
                    $"{file.RelativePath} line {index}: '{line}' is neither a key/value nor a list " +
                    "item, so ZCode's line parser would drop it.");
            }
        }
    }

    /// <summary>
    /// <c>.zcode/agents/</c> and <c>.zcode/commands/</c> are both scanned recursively, so a
    /// resource under either would register as a phantom agent or command.
    /// </summary>
    [Fact]
    public async Task RenderAsync_ZCode_KeepsTheRecursivelyScannedRootsFlat()
    {
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        foreach (string prefix in new[] { ".zcode/agents/", ".zcode/commands/" })
        {
            foreach (SquadDeploymentFile file in result.Files
                         .Where(f => f.RelativePath.StartsWith(prefix, StringComparison.Ordinal)))
            {
                Assert.DoesNotContain(
                    '/',
                    file.RelativePath[prefix.Length..]);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_ZCode_ProjectsAgentResourcesUnderSkillsAndRewritesTheirLinks()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        List<SquadAgent> withResources = source.Agents.Where(a => a.Resources.Count > 0).ToList();
        Assert.NotEmpty(withResources);

        foreach (SquadAgent agent in withResources)
        {
            foreach (SquadResource resource in agent.Resources)
            {
                SquadDeploymentFile projected = Assert.Single(
                    result.Files, f => f.RelativePath == $".zcode/skills/{resource.RelativePath}");
                Assert.Equal(resource.Content, Encoding.UTF8.GetString(projected.Content.Span));

                // The authored link is relative to the owner's own directory; after the move it
                // has to climb out of agents/ or commands/ and into skills/.
                SquadDeploymentFile principal = Assert.Single(
                    result.Files,
                    f => f.RelativePath == $".zcode/agents/{agent.Name}.md" ||
                         f.RelativePath == $".zcode/commands/{agent.Name}.md");
                string body = DocumentText(principal);
                Assert.DoesNotContain($"]({resource.RelativePath})", body, StringComparison.Ordinal);
                Assert.Contains($"](../skills/{resource.RelativePath})", body, StringComparison.Ordinal);
            }

            SquadDegradationRecord record = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == agent.Name && d.Code == "resource-links-rewritten");
            Assert.Equal(agent.BodyDigest, record.InstructionDigest);
        }

        // An agent without resources records nothing: the deviation is only reported where it
        // actually happened.
        foreach (SquadAgent agent in source.Agents.Where(a => a.Resources.Count == 0))
        {
            Assert.DoesNotContain(
                result.Degradations,
                d => d.CanonicalIdentity == agent.Name && d.Code == "resource-links-rewritten");
        }
    }

    [Fact]
    public async Task RenderAsync_ZCode_SkillResourcesStayBesideTheirSkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        HashSet<string> shared = SharedIdentities(source);
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        foreach (SquadSkill skill in source.Skills.Where(s => !shared.Contains(s.Name) && s.Resources.Count > 0))
        {
            foreach (SquadResource resource in skill.Resources)
            {
                SquadDeploymentFile projected = Assert.Single(
                    result.Files,
                    f => f.RelativePath == $".zcode/skills/{skill.Name}/{resource.RelativePath}");
                Assert.Equal(resource.Content, Encoding.UTF8.GetString(projected.Content.Span));
            }
        }
    }

    [Fact]
    public async Task RenderAsync_ZCodeGlobalScope_DropsTheDotZcodePrefixFromEverySubtree()
    {
        SquadRenderResult project = await RenderZCodeAsync(ProductRoot);
        SquadRenderResult global = await RenderZCodeAsync(ProductRoot, SquadDeploymentScope.Global);

        Assert.True(global.Success, string.Join("; ", global.Errors));
        Assert.Equal(
            project.Files.Select(f => f.RelativePath[".zcode/".Length..]).Order(StringComparer.Ordinal),
            global.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal));

        // The rewritten link form has to be identical under both scopes: agents/, commands/
        // and skills/ sit one level below the same root either way.
        SquadDeploymentFile globalCommand = Assert.Single(
            global.Files, f => f.RelativePath.StartsWith("commands/", StringComparison.Ordinal));
        Assert.Contains("](../skills/", DocumentText(globalCommand), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_ZCode_ResolvesTheHarnessModelOverrideAndOmitsInherit()
    {
        using ZCodeModelOverrideFixture fixture = ZCodeModelOverrideFixture.Create();

        SquadRenderResult result = await RenderZCodeAsync(fixture.ProductRoot);

        SquadDeploymentFile overridden = Assert.Single(
            result.Files, f => f.RelativePath == $".zcode/agents/{ZCodeModelOverrideFixture.OverriddenAgentName}.md");
        (Dictionary<string, string> overriddenScalars, _) =
            ParseFrontmatter(DocumentText(overridden), ZCodeModelOverrideFixture.OverriddenAgentName);
        Assert.Equal(ZCodeModelOverrideFixture.OverrideModel, overriddenScalars["model"]);

        SquadDeploymentFile inherited = Assert.Single(
            result.Files, f => f.RelativePath == $".zcode/agents/{ZCodeModelOverrideFixture.InheritAgentName}.md");
        (Dictionary<string, string> inheritedScalars, _) =
            ParseFrontmatter(DocumentText(inherited), ZCodeModelOverrideFixture.InheritAgentName);
        Assert.DoesNotContain("model", inheritedScalars.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// A canonical skill owning the directory an agent's rewritten closure would claim is a
    /// fail-closed condition: the two would share <c>.zcode/skills/&lt;name&gt;/</c>, where a
    /// resource could overwrite the skill's own file.
    /// </summary>
    [Fact]
    public async Task RenderAsync_ZCode_ThrowsWhenACanonicalSkillOccupiesARewrittenResourceRoot()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent owner = source.Agents.First(agent => agent.Resources.Count > 0);

        using ZCodeResourceRootCollisionFixture fixture = ZCodeResourceRootCollisionFixture.Create(owner.Name);

        SquadRenderValidationException exception =
            await Assert.ThrowsAsync<SquadRenderValidationException>(
                () => RenderZCodeAsync(fixture.ProductRoot));

        Assert.Contains(owner.Name, exception.Message, StringComparison.Ordinal);
        Assert.Contains(".zcode/skills/", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the one lossy edge of the emitted scalar form, so it stays a deliberate,
    /// upstream-matching choice rather than drifting into an unnoticed corruption.
    /// </summary>
    [Fact]
    public async Task RenderAsync_ZCode_EscapesEmbeddedQuotesTheWayZCodeEscapesItsOwn()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRenderResult result = await RenderZCodeAsync(ProductRoot);

        List<SquadSkill> quoted = source.Skills
            .Where(skill => skill.Description.Contains('"', StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(quoted);

        foreach (SquadSkill skill in quoted)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files, f => f.RelativePath == $".zcode/skills/{skill.Name}/SKILL.md");
            string document = DocumentText(file);

            // Emitted exactly as ZCode's own writer would: a double-quoted scalar with the
            // inner quotes backslash-escaped.
            Assert.Contains("\\\"", document, StringComparison.Ordinal);

            // And read back with the backslash still attached, because neither ZCode reader
            // unescapes. Apostrophes, by contrast, survive untouched inside the double quotes.
            (Dictionary<string, string> scalars, _) = ParseFrontmatter(document, skill.Name);
            Assert.Equal(AsZCodeReadsIt(skill.Description), scalars["description"]);
            Assert.DoesNotContain("\\'", scalars["description"], StringComparison.Ordinal);
        }
    }

    private static bool IsPrincipal(SquadDeploymentFile file) =>
        file.RelativePath.StartsWith(".zcode/agents/", StringComparison.Ordinal) ||
        file.RelativePath.StartsWith(".zcode/commands/", StringComparison.Ordinal) ||
        file.RelativePath.EndsWith("/SKILL.md", StringComparison.Ordinal);
}

/// <summary>
/// Copies the canonical corpus and rewrites two model profiles' <c>zcode</c> values, so the
/// harness-override and <c>inherit</c> branches are exercised against a fixture rather than
/// against whatever <c>models.yml</c> happens to declare today.
/// </summary>
internal sealed class ZCodeModelOverrideFixture : IDisposable
{
    internal const string OverriddenProfile = "general";
    internal const string OverriddenAgentName = "dal-dev";
    internal const string OverrideModel = "acme/zcode-test-model";
    internal const string InheritProfile = "fast";
    internal const string InheritAgentName = "csharp-dev";

    private readonly TempDirectory _temp = new();

    private ZCodeModelOverrideFixture() => ProductRoot = Path.Combine(_temp.Path, "kyber-squad");

    internal string ProductRoot { get; }

    internal static ZCodeModelOverrideFixture Create()
    {
        ZCodeModelOverrideFixture fixture = new();
        ZCodeFixtureFiles.CopyCanonicalCorpus(fixture.ProductRoot);

        string modelsPath = Path.Combine(fixture.ProductRoot, "profiles", "models.yml");
        string original = File.ReadAllText(modelsPath);
        string mutated = SetZCodeValue(original, OverriddenProfile, OverrideModel);
        mutated = SetZCodeValue(mutated, InheritProfile, "inherit");

        if (string.Equals(original, mutated, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected '{modelsPath}' to contain profiles '{OverriddenProfile}' and " +
                $"'{InheritProfile}' to mutate.");
        }

        File.WriteAllText(modelsPath, mutated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fixture;
    }

    /// <summary>
    /// Replaces the profile's existing <c>zcode:</c> line. The canonical corpus declares one
    /// for every profile, so an insert branch would be untested code; a missing line is a
    /// fixture bug and throws.
    /// </summary>
    private static string SetZCodeValue(string content, string profileName, string value)
    {
        string[] lines = content.Split('\n');
        int start = Array.FindIndex(lines, line => line == $"  {profileName}:");
        if (start < 0)
        {
            throw new InvalidOperationException($"Profile '{profileName}' not found in models.yml.");
        }

        for (int index = start + 1; index < lines.Length && lines[index].StartsWith("    ", StringComparison.Ordinal); index++)
        {
            if (lines[index].StartsWith("    zcode:", StringComparison.Ordinal))
            {
                lines[index] = $"    zcode: {value}";
                return string.Join('\n', lines);
            }
        }

        throw new InvalidOperationException($"Profile '{profileName}' declares no zcode value.");
    }

    public void Dispose() => _temp.Dispose();
}

/// <summary>
/// Copies the canonical corpus and adds a skill whose name collides with the directory an
/// agent's rewritten resource closure would claim under <c>.zcode/skills/</c>.
/// </summary>
internal sealed class ZCodeResourceRootCollisionFixture : IDisposable
{
    private readonly TempDirectory _temp = new();

    private ZCodeResourceRootCollisionFixture() => ProductRoot = Path.Combine(_temp.Path, "kyber-squad");

    internal string ProductRoot { get; }

    internal static ZCodeResourceRootCollisionFixture Create(string agentName)
    {
        ZCodeResourceRootCollisionFixture fixture = new();
        ZCodeFixtureFiles.CopyCanonicalCorpus(fixture.ProductRoot);

        string skillDirectory = Path.Combine(fixture.ProductRoot, "skills", agentName);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            $"""
             ---
             name: {agentName}
             description: Use when a canonical skill deliberately occupies an agent's rewritten resource root.
             license: MIT
             ---
             Fixture body for the '{agentName}' resource-root collision.
             """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return fixture;
    }

    public void Dispose() => _temp.Dispose();
}

internal static class ZCodeFixtureFiles
{
    internal static void CopyCanonicalCorpus(string destinationDirectory)
    {
        string canonicalRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(canonicalRoot, "*", SearchOption.AllDirectories))
        {
            string destinationPath = Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(canonicalRoot, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }
}
