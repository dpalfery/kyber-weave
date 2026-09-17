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

    public void Dispose()
    {
        // No disposable state: suite reads canonical checked-in corpus.
        // Implements IDisposable per repository test coding standard.
        GC.SuppressFinalize(this);
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

        int expectedFileCount = source.Agents.Count + source.Agents.Sum(agent => agent.Resources.Count)
            + expectedSkillCount
            + source.Skills.Where(skill => !sharedIdentities.Contains(skill.Name))
                .Sum(skill => skill.Resources.Count);
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
            Assert.False(
                frontmatter.Children.ContainsKey(new YamlScalarNode("reasoningEffort")),
                $"Agent '{agent.Name}' should omit 'reasoningEffort'.");
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
                    Assert.Equal(factoryHarnessModel, RequireScalar(frontmatter, "model", agent.Name));
                }
            }
            else if (!string.Equals(modelProfile.Default, "inherit", StringComparison.Ordinal) &&
                     !string.IsNullOrWhiteSpace(modelProfile.Default))
            {
                Assert.Equal(modelProfile.Default, RequireScalar(frontmatter, "model", agent.Name));
            }
            else
            {
                Assert.False(
                    frontmatter.Children.ContainsKey(new YamlScalarNode("model")),
                    $"Agent '{agent.Name}' should omit 'model' (profile default is inherit).");
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
