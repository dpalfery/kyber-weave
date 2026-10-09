using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Squad.Parsing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.2 (embedded Squad catalog): <c>ArbiterSquadCatalog</c> reads the
/// embedded agent copies (delegates-to, description, capability profile, target class)
/// and lens Applicability sections, and equals the canonical
/// <c>products/kyber-squad</c> tree.
/// RED: the embedded agent/lens resources and the catalog do not exist yet.
/// </summary>
public sealed class ArbiterSquadCatalogTests
{
    [Fact]
    public void EmbeddedCatalog_HasEveryCanonicalAgent()
    {
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        string[] expected =
        [
            "architect", "azure-reader", "bug-crusher-investigator", "code-reviewer", "conductor",
            "csharp-dev", "dal-dev", "docs-dev", "github-devops", "maui-dev", "product-owner",
            "pulumi-dev", "python-dev", "react-dev", "research-agent", "review-lens",
            "review-triage", "sql-database-architect", "task-reviewer", "tauri-dev", "test-dev",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), catalog.Agents.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EmbeddedCatalog_CarriesDelegatesToDescriptionAndProfile()
    {
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        ArbiterSquadAgent conductor = catalog.Agents["conductor"];
        Assert.Contains("architect", conductor.DelegatesTo);
        Assert.Contains("test-dev", conductor.DelegatesTo);
        Assert.Contains("orchestrat", conductor.Description, StringComparison.OrdinalIgnoreCase);

        ArbiterSquadAgent csharp = catalog.Agents["csharp-dev"];
        Assert.Empty(csharp.DelegatesTo);
        Assert.Equal("worker", csharp.CapabilityProfile);
        Assert.Contains(".NET", csharp.Description, StringComparison.Ordinal);

        ArbiterSquadAgent reviewer = catalog.Agents["code-reviewer"];
        Assert.Equal(
            ["azure-reader", "review-lens", "review-triage"],
            reviewer.DelegatesTo.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TargetClass_FollowsTheSpecTable()
    {
        Assert.Equal("implementation", ArbiterSquadCatalog.TargetClassFor("worker"));
        Assert.Equal("implementation", ArbiterSquadCatalog.TargetClassFor("publishing-worker"));
        Assert.Equal("implementation", ArbiterSquadCatalog.TargetClassFor("documentation"));
        Assert.Equal("planner", ArbiterSquadCatalog.TargetClassFor("architect"));
        Assert.Equal("planner", ArbiterSquadCatalog.TargetClassFor("product-planning"));
        Assert.Equal("read-only", ArbiterSquadCatalog.TargetClassFor("investigator"));
        Assert.Equal("read-only", ArbiterSquadCatalog.TargetClassFor("read-only"));
        Assert.Equal("read-only", ArbiterSquadCatalog.TargetClassFor("reviewer"));
        Assert.Equal("not-squad", ArbiterSquadCatalog.TargetClassFor("orchestrator"));

        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        Assert.Equal("implementation", catalog.Agents["csharp-dev"].TargetClass);
        Assert.Equal("planner", catalog.Agents["architect"].TargetClass);
        Assert.Equal("read-only", catalog.Agents["code-reviewer"].TargetClass);
    }

    [Fact]
    public void EmbeddedCatalog_HasEveryLensApplicability()
    {
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        string[] expected =
        [
            "authz-tenancy", "blast-radius-revertibility", "correctness", "dependency-supply-chain",
            "di-composition", "duplicate-implementation", "infra-workflow", "intent-alignment",
            "model-placement", "performance", "prior-art", "security",
            "static-analysis-triage", "supportability", "test-adequacy",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), catalog.Lenses.Keys.Order(StringComparer.Ordinal));
        Assert.All(catalog.Lenses.Values, lens => Assert.False(string.IsNullOrWhiteSpace(lens.Applicability)));
        Assert.Contains(
            "untrusted input",
            catalog.Lenses["security"].Applicability,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_EqualsTheCanonicalTree()
    {
        string squadRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
        var source = SquadSourceLoader.Load(squadRoot);
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        Assert.Equal(source.Agents.Count, catalog.Agents.Count);
        foreach (var agent in source.Agents)
        {
            Assert.True(catalog.Agents.TryGetValue(agent.Name, out ArbiterSquadAgent? entry), $"Missing agent '{agent.Name}'.");
            Assert.Equal(agent.Description, entry.Description);
            Assert.Equal(
                agent.DelegatesTo.Order(StringComparer.Ordinal),
                entry.DelegatesTo.Order(StringComparer.Ordinal));
            Assert.Equal(agent.CapabilityProfile, entry.CapabilityProfile);
        }

        foreach (string lensFile in Directory.EnumerateFiles(
                     Path.Combine(squadRoot, "skills", "code-review", "references", "lenses"), "*.md"))
        {
            string name = Path.GetFileNameWithoutExtension(lensFile);
            string expected = ArbiterSquadCatalog.ReadApplicabilitySection(File.ReadAllText(lensFile));
            Assert.True(catalog.Lenses.TryGetValue(name, out ArbiterSquadLens? entry), $"Missing lens '{name}'.");
            Assert.Equal(expected, entry.Applicability);
        }
    }
}
