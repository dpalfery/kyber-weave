using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real, checked-in canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="WarpRenderer"/> and pins the fallback role-skill contract
/// (skills under <c>.warp/skills/</c>, no native agent files).
/// </summary>
/// <remarks>
/// Counts and collision sets are derived from the loaded <see cref="SquadSource"/> so the
/// suite tracks the shipped corpus rather than hardcoded literals that would silently drift.
/// </remarks>
public sealed class WarpRendererContractTests : IDisposable
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

    /// <summary>
    /// Warp's own decisions on the real corpus: with no agent primitive, every agent lowers onto a
    /// skill — a shared identity reuses its canonical skill, a distinct-body collision ships beside
    /// it as <c>role-&lt;name&gt;</c> carrying the agent body, and every other agent under its own
    /// name — each with one <c>role-skill-fallback</c> record, and every non-deny decision is
    /// recorded as inexpressible. The canonical skill documents are the shared pipeline's, checked
    /// in <see cref="Squad.SquadRendererSharedContractTests"/>.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Warp_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> shared = SharedIdentities(source).ToHashSet(StringComparer.Ordinal);
        HashSet<string> collisions = DeriveCollisions(source);
        Assert.NotEmpty(collisions);

        SquadRenderResult result = await new SquadRendererRegistry([new WarpRenderer()]).RenderAsync(
            new SquadRenderRequest(ProductRoot, [SquadTarget.Warp], SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("/agents/", StringComparison.OrdinalIgnoreCase));
        foreach (SquadAgent agent in source.Agents)
        {
            string outputIdentity = collisions.Contains(agent.Name) ? $"role-{agent.Name}" : agent.Name;
            SquadDegradationRecord fallback = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == agent.Name && d.Code == "role-skill-fallback");
            Assert.Equal(outputIdentity, fallback.OutputIdentity);

            if (shared.Contains(agent.Name))
            {
                Assert.DoesNotContain(result.Files, f => f.RelativePath == $".warp/skills/role-{agent.Name}/SKILL.md");
                continue;
            }

            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".warp/skills/{outputIdentity}/SKILL.md");
            Assert.Equal(outputIdentity, RequireScalar(ReadFrontmatter(file), "name"));
            Assert.Equal(NormalizeBody(agent.InstructionBody), ReadBody(file));
        }

        Assert.Equal(
            source.Agents
                .Where(a => HasNonDenyCapability(source.CapabilityProfiles, source.CapabilityProfiles.Profiles[a.CapabilityProfile]))
                .Select(a => a.Name)
                .Order(StringComparer.Ordinal),
            result.Degradations
                .Where(d => d.Code == "permission-not-expressible")
                .Select(d => d.CanonicalIdentity)
                .Order(StringComparer.Ordinal));
    }
    private static string NormalizeBody(string body)
    {
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
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
}
