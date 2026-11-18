using KyberWeave.Testing.Squad;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Squad.Parsing;
using Xunit;
using KyberWeave.Tests.Corpus;

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
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void EmbeddedCatalog_HasEveryCanonicalAgent()
    {
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        string[] expected =
        [
            "architect", "bug-crusher", "bug-crusher-investigator", "code-reviewer", "conductor",
            "dev-easy", "dev-hard", "dev-medium", "devops", "docs-dev", "platform-researcher",
            "product-owner", "research-agent", "review-lens", "review-triage",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), catalog.Agents.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EmbeddedCatalog_CarriesDelegatesToAndProfile()
    {
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        ArbiterSquadAgent conductor = catalog.Agents["conductor"];
        Assert.Contains("architect", conductor.DelegatesTo);
        Assert.Contains("dev-hard", conductor.DelegatesTo);

        ArbiterSquadAgent developer = catalog.Agents["dev-medium"];
        Assert.Empty(developer.DelegatesTo);
        Assert.Equal("worker", developer.CapabilityProfile);

        ArbiterSquadAgent reviewer = catalog.Agents["code-reviewer"];
        Assert.Equal(
            ["platform-researcher", "review-lens", "review-triage"],
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
        Assert.Equal("implementation", catalog.Agents["dev-medium"].TargetClass);
        Assert.Equal("planner", catalog.Agents["architect"].TargetClass);
        Assert.Equal("read-only", catalog.Agents["code-reviewer"].TargetClass);
    }

    [Fact]
    public void PlatformReader_ClassifiesAsReadOnly_BecauseItCannotWriteItsWork()
    {
        Assert.Equal("read-only", ArbiterSquadCatalog.TargetClassFor("platform-reader"));

        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        Assert.Equal("read-only", catalog.Agents["platform-researcher"].TargetClass);
    }

    [Fact]
    public void EmbeddedCatalog_HasEveryLensApplicability()
    {
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();

        string[] expected =
        [
            "authz-tenancy", "blast-radius-revertibility", "correctness", "dependency-supply-chain",
            "di-composition", "documentation-accuracy", "duplicate-implementation", "infra-workflow",
            "intent-alignment", "model-placement", "performance", "prior-art", "security",
            "static-analysis-triage", "supportability", "test-adequacy", "test-infrastructure",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), catalog.Lenses.Keys.Order(StringComparer.Ordinal));
        Assert.All(catalog.Lenses.Values, lens => Assert.False(string.IsNullOrWhiteSpace(lens.Applicability)));
    }

    [Fact]
    public void Catalog_EqualsTheCanonicalTree()
    {
        string squadRoot = Path.Combine(Corpus.Root, "products", "kyber-squad");
        var source = RealSquadServices.Load(squadRoot);
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

        foreach (string lensFile in Corpus.EnumerateFiles(
                     "products/kyber-squad/skills/code-review/references/lenses", "*.md", recursive: false))
        {
            string name = Path.GetFileNameWithoutExtension(lensFile);
            string expected = ArbiterSquadCatalog.ReadApplicabilitySection(Corpus.ReadText(lensFile));
            Assert.True(catalog.Lenses.TryGetValue(name, out ArbiterSquadLens? entry), $"Missing lens '{name}'.");
            Assert.Equal(expected, entry.Applicability);
        }
    }
}
