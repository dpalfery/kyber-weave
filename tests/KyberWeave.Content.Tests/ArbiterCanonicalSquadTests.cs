using KyberWeave.Testing.Squad;
using System.Reflection;
using System.Text.Json;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for task 17.3 (design §10.6): <c>decision.query</c> is in the canonical
/// vocabulary, every profile declares it, and the Arbiter server is declared in <c>mcp.json</c>
/// and <c>toolchain.yml</c>. That no hooked target grants the server is checked where the
/// renderers are tested.
/// </summary>
public sealed class ArbiterCanonicalSquadTests
{
    private const string DecisionQuery = "decision.query";
    private const string ArbiterServer = "kw-arbiter";

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
        "platform-reader",
        "product-planning",
        "product-planning-devin",
        "publishing-worker",
        "read-only",
        "review-seat",
        "root-fixer",
        "worker",
    ];

    [Fact]
    public void DecisionQueryJoinsTheCapabilityVocabulary()
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

        Assert.Contains(DecisionQuery, source.CapabilityProfiles.Capabilities);
    }

    [Fact]
    public void EveryProfileDeclaresDecisionQueryAllowForConductorAndReviewerAndDenyForTheRest()
    {
        SquadSource source = CanonicalSquadTree.SharedSource;

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
        SquadSource source = CanonicalSquadTree.SharedSource;

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
        SquadSource source = CanonicalSquadTree.SharedSource;

        string[] declared = DeclaredArbiterToolNames();
        string[] pinned = [.. source.Toolchain.RequiredMcpTools[ArbiterServer].Order(StringComparer.Ordinal)];

        Assert.Equal(declared, pinned);
    }

    private static string[] DeclaredArbiterToolNames() =>
        [.. typeof(ArbiterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name!)
            .Order(StringComparer.Ordinal)];
}
