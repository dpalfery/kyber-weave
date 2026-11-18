using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the widened <c>codegraph</c> grant on the two targets that gate MCP reach by permission key
/// (D26), and the one it must not reach.
/// </summary>
/// <remarks>
/// <para>
/// OpenCode and Kilo give an agent a permission map rather than a tool list. Before this change neither
/// named the code graph at all, so a developer on either harness could not query the index its own
/// instructions told it to use. The grant goes through the same shared predicate every other target
/// uses, so the orchestrator keeps its carve-out for the same reason it does everywhere else.
/// </para>
/// <para>
/// <c>context7</c> is asserted absent beside each grant on purpose. The owner widened the code graph, not
/// library lookups, and a permission map is the easiest place to widen both by accident: one shared
/// predicate, two server names, and nothing in a renderer-local test would notice.
/// </para>
/// </remarks>
public sealed class SquadCodeGraphGrantContractTests
{
    /// <summary>The server whose reach is widened, and the one that keeps the carve-out it had.</summary>
    private const string CodeGraphServer = "codegraph";

    private const string Context7Server = "context7";

    /// <summary>Each target's agent file, where the permission map lives.</summary>
    public static TheoryData<SquadTarget, string> PermissionKeyTargets => new()
    {
        { SquadTarget.OpenCode, ".opencode/agents/{0}.md" },
        { SquadTarget.Kilo, ".kilo/agents/{0}.md" }
    };

    /// <summary>
    /// Every entitled agent's permission map carries the code graph wildcard, and no agent's map carries
    /// the library-lookup server.
    /// </summary>
    /// <remarks>
    /// The expectation comes from the shared predicate rather than a list of agent names, so adding a
    /// delegate or narrowing a profile moves the expectation with the renderer. The assertion is on the
    /// agent's own map precisely because the map is the grant vehicle: a key that is absent withholds the
    /// tools, whatever the renderer believes it emitted.
    /// </remarks>
    [Theory]
    [MemberData(nameof(PermissionKeyTargets))]
    public async Task EveryEntitledAgentCarriesTheCodeGraphPermissionAndNoneCarriesContext7(
        SquadTarget target,
        string agentPathFormat)
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            string relativePath = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                agentPathFormat,
                agent.Name);

            SquadDeploymentFile file = Assert.Single(result.Files, candidate => candidate.RelativePath == relativePath);
            IReadOnlyList<string> permissions = PermissionKeys(file);

            bool entitled =
                SquadMcpGrantProjection.GrantsOrchestratorExcludedServers(source, agent, target);
            bool granted = permissions.Any(key =>
                key.StartsWith($"{CodeGraphServer}_", StringComparison.Ordinal));

            Assert.True(
                entitled == granted,
                $"'{SquadTargetCatalog.GetToken(target)}' renders agent '{agent.Name}' with the code graph "
                + $"{(granted ? "granted" : "withheld")}, but the shared predicate requires it "
                + $"{(entitled ? "granted" : "withheld")}.");

            Assert.DoesNotContain(
                permissions,
                key => key.StartsWith($"{Context7Server}_", StringComparison.Ordinal));
        }
    }

    /// <summary>The keys of the rendered <c>permission</c> map, which is where this harness gates MCP reach.</summary>
    private static IReadOnlyList<string> PermissionKeys(SquadDeploymentFile file)
    {
        string content = Encoding.UTF8.GetString(file.Content.Span);
        int end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
        Assert.True(end > 0, $"'{file.RelativePath}' has no YAML frontmatter to read a permission map from.");

        YamlStream yaml = new();
        yaml.Load(new StringReader(content[4..end]));
        YamlMappingNode root = (YamlMappingNode)yaml.Documents[0].RootNode;

        if (!root.Children.TryGetValue(new YamlScalarNode("permission"), out YamlNode? permission)
            || permission is not YamlMappingNode map)
        {
            return [];
        }

        return [.. map.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value ?? string.Empty)];
    }
}
