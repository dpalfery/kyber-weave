using System.Reflection;
using System.Text;
using System.Text.Json;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for task 17.3 (design §10.6): <c>decision.query</c> is in the canonical
/// vocabulary, every profile declares it, the Arbiter server is declared in <c>mcp.json</c>
/// and <c>toolchain.yml</c>, and no hooked target grants the server.
/// </summary>
public sealed class ArbiterCanonicalSquadTests
{
    private const string DecisionQuery = "decision.query";
    private const string ArbiterServer = "kyber-weave-arbiter";

    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly string[] AllowedProfiles = ["orchestrator", "reviewer"];

    // Extended, never pruned: main's two target-scoped Devin profiles arrived with
    // the merge and deny decision.query, because Devin's qualified generic grants
    // deliberately exclude the Arbiter server (ADR 0028).
    private static readonly string[] DeniedProfiles =
    [
        "architect",
        "architect-copilot",
        "architect-devin",
        "documentation",
        "investigator",
        "product-planning",
        "product-planning-devin",
        "publishing-worker",
        "read-only",
        "worker",
    ];

    [Fact]
    public void DecisionQueryJoinsTheCapabilityVocabulary()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.Contains(DecisionQuery, source.CapabilityProfiles.Capabilities);
    }

    [Fact]
    public void EveryProfileDeclaresDecisionQueryAllowForConductorAndReviewerAndDenyForTheRest()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.Equal(
            (AllowedProfiles.Concat(DeniedProfiles)).Order(StringComparer.Ordinal),
            source.CapabilityProfiles.Profiles.Keys.Order(StringComparer.Ordinal));

        foreach (string profile in AllowedProfiles)
        {
            Assert.Equal(
                SquadPermissionDecision.Allow,
                source.CapabilityProfiles.Profiles[profile].Permissions[DecisionQuery]);
        }

        foreach (string profile in DeniedProfiles)
        {
            Assert.Equal(
                SquadPermissionDecision.Deny,
                source.CapabilityProfiles.Profiles[profile].Permissions[DecisionQuery]);
        }
    }

    [Fact]
    public void McpConfigurationDeclaresTheArbiterServerAsServeAtTheRepositoryRoot()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        JsonElement server = source.McpConfiguration
            .GetProperty("mcpServers")
            .GetProperty(ArbiterServer);

        Assert.Equal("kyber-weave-arbiter", server.GetProperty("command").GetString());
        Assert.Equal(
            ["serve", "--repo-root", "."],
            server.GetProperty("args").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public void ToolchainArbiterToolsEqualTheToolsArbiterToolsDeclares()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        string[] declared = DeclaredArbiterToolNames();
        string[] pinned = [.. source.Toolchain.RequiredMcpTools[ArbiterServer].Order(StringComparer.Ordinal)];

        Assert.Equal(declared, pinned);
    }

    [Fact]
    public async Task NoHookedTargetGrantsTheArbiterServer()
    {
        string[] qualifiedTools = DeclaredArbiterToolNames()
            .Select(tool => $"mcp__{ArbiterServer}__{tool}")
            .ToArray();
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();

        foreach (SquadTarget target in Enum.GetValues<SquadTarget>())
        {
            if (!ArbiterHookWiring.HookedTargets.Contains(SquadTargetCatalog.GetToken(target)))
            {
                continue;
            }

            SquadRenderResult result = await renderer.RenderAsync(new SquadRenderRequest(
                SourceDirectory: ProductRoot,
                Targets: [target],
                Scope: SquadDeploymentScope.Project));
            Assert.True(result.Success, string.Join("; ", result.Errors));

            foreach (SquadDeploymentFile file in result.Files)
            {
                string content = Encoding.UTF8.GetString(file.Content.Span);

                foreach (string tool in qualifiedTools)
                {
                    Assert.DoesNotContain(tool, content, StringComparison.Ordinal);
                }

                Assert.DoesNotContain($"{ArbiterServer}/", content, StringComparison.Ordinal);
            }
        }
    }

    private static string[] DeclaredArbiterToolNames() =>
        [.. typeof(ArbiterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name!)
            .Order(StringComparer.Ordinal)];
}
