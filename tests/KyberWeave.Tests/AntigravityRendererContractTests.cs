using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real, checked-in canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="AntigravityRenderer"/> and pins the fallback role-skill contract
/// (skills under <c>.agents/skills/</c>, no native agent files).
/// </summary>
/// <remarks>
/// Counts and collision sets are derived from the loaded <see cref="SquadSource"/> so the
/// suite tracks the shipped corpus rather than hardcoded 22/26/46 literals that would
/// silently drift.
/// </remarks>
public sealed class AntigravityRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly string[] SharedConductorIdentities = ["conductor", "conductor-v3"];

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
        return source.Agents
            .Where(a => skillNames.Contains(a.Name) && !SharedConductorIdentities.Contains(a.Name))
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
        HashSet<string> shared = SharedConductorIdentities.ToHashSet(StringComparer.Ordinal);
        HashSet<string> collisions = DeriveCollisions(source);

        int unoccupiedAgents = source.Agents.Count(a => !shared.Contains(a.Name) && !collisions.Contains(a.Name));
        int expectedFiles = source.Skills.Count + unoccupiedAgents + collisions.Count;

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

        Assert.All(result.Files, f =>
        {
            Assert.Equal("antigravity", f.Target);
            Assert.StartsWith(".agents/skills/", f.RelativePath, StringComparison.Ordinal);
            Assert.EndsWith("/SKILL.md", f.RelativePath, StringComparison.Ordinal);
        });

        foreach (SquadSkill skill in source.Skills)
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/skills/{skill.Name}/SKILL.md");

            YamlMappingNode frontmatter = ReadFrontmatter(file);
            Assert.Equal(skill.Name, RequireScalar(frontmatter, "name"));
            Assert.Equal(ToSingleLineScalar(skill.Description), RequireScalar(frontmatter, "description"));
            Assert.Equal("MIT", RequireScalar(frontmatter, "license"));
            Assert.False(frontmatter.Children.ContainsKey(new YamlScalarNode("model")));

            string expectedBody = NormalizeBody(skill.InstructionBody);
            Assert.Equal(expectedBody, ReadBody(file));
        }

        foreach (string collision in collisions)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".agents/skills/{collision}/SKILL.md");
            SquadDeploymentFile roleFile = Assert.Single(
                result.Files,
                f => f.RelativePath == $".agents/skills/role-{collision}/SKILL.md");

            SquadAgent agent = Assert.Single(source.Agents, a => a.Name == collision);
            YamlMappingNode frontmatter = ReadFrontmatter(roleFile);
            Assert.Equal($"role-{collision}", RequireScalar(frontmatter, "name"));
            Assert.Equal(ToSingleLineScalar(agent.Description), RequireScalar(frontmatter, "description"));
            Assert.Equal("MIT", RequireScalar(frontmatter, "license"));
            Assert.Equal(NormalizeBody(agent.InstructionBody), ReadBody(roleFile));
        }

        foreach (string conductor in SharedConductorIdentities)
        {
            Assert.Contains(result.Files, f => f.RelativePath == $".agents/skills/{conductor}/SKILL.md");
            Assert.DoesNotContain(result.Files, f => f.RelativePath == $".agents/skills/role-{conductor}/SKILL.md");
            Assert.Equal(
                1,
                result.Files.Count(f => f.RelativePath.Contains($"/{conductor}/SKILL.md", StringComparison.Ordinal)));

            SquadDegradationRecord fallback = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == conductor && d.Code == "role-skill-fallback");
            Assert.Equal(conductor, fallback.OutputIdentity);
        }

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDegradationRecord fallback = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == agent.Name && d.Code == "role-skill-fallback");
            Assert.Equal("antigravity", fallback.Target);
            Assert.Equal(agent.BodyDigest, fallback.InstructionDigest);

            if (collisions.Contains(agent.Name))
            {
                Assert.Equal($"role-{agent.Name}", fallback.OutputIdentity);
            }
            else
            {
                Assert.Equal(agent.Name, fallback.OutputIdentity);
            }
        }

        string[] expectedPermissionAgents = source.Agents
            .Where(a => HasNonDenyCapability(source.CapabilityProfiles, source.CapabilityProfiles.Profiles[a.CapabilityProfile]))
            .Select(a => a.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] actualPermissionAgents = result.Degradations
            .Where(d => d.Code == "permission-not-expressible")
            .Select(d => d.CanonicalIdentity)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedPermissionAgents, actualPermissionAgents);

        foreach (SquadDegradationRecord degradation in result.Degradations.Where(d => d.Code == "permission-not-expressible"))
        {
            Assert.Equal("antigravity", degradation.Target);
            Assert.Equal(degradation.CanonicalIdentity, degradation.OutputIdentity);
            SquadAgent agent = Assert.Single(source.Agents, a => a.Name == degradation.CanonicalIdentity);
            Assert.Equal(agent.BodyDigest, degradation.InstructionDigest);
            Assert.DoesNotContain("widening", degradation.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("widened", degradation.Code, StringComparer.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("/agents/", StringComparison.OrdinalIgnoreCase));
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
    }

    private static string NormalizeBody(string body)
    {
        string normalized = body.Replace("\r\n", "\n");
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
        YamlNode value = node.Children[new YamlScalarNode(key)];
        return Assert.IsType<YamlScalarNode>(value).Value
            ?? throw new InvalidOperationException($"Key '{key}' has a null scalar value.");
    }
}
