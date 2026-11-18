using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using Xunit;
using YamlDotNet.Core;
using KyberWeave.Tests;

namespace KyberWeave.Application.Tests.Configuration;

/// <summary>
/// The registry a portable skill resolves names against. What it must never do is publish a
/// name that points nowhere, because the failure surfaces in a host repository where nobody
/// can see the configuration that caused it.
/// </summary>
public class ConfigRegResolutionTests
{
    private static OntologyConfig Ontology(params string[] technologies) =>
        new OntologyConfig { DocsRoots = ["docs"], Technologies = technologies };

    private static string PathOf(IReadOnlyList<ConfigRegEntry> entries, string name) =>
        entries.Single(e => e.Name == name).Path;

    [Fact]
    public void EveryScaffoldedPathIsPublishedUnderAStableName()
    {
        IReadOnlyList<ConfigRegEntry> entries = ConfigRegConfig.ProductDefaults.Resolve(Ontology());

        Assert.Equal("docs", PathOf(entries, ConfigRegConfig.DocsRootProperty));
        Assert.Equal("docs/README.md", PathOf(entries, ConfigRegConfig.DocumentationIndexProperty));
        Assert.Equal("docs/documentation-ontology.md", PathOf(entries, ConfigRegConfig.DocumentationOntologyProperty));
        Assert.Equal("docs/catalog.md", PathOf(entries, ConfigRegConfig.ComponentCatalogProperty));
        Assert.Equal("docs/standards", PathOf(entries, ConfigRegConfig.StandardsRootProperty));
        Assert.Equal("docs/plans/README.md", PathOf(entries, "plan-index"));
        Assert.Equal("docs/adr/README.md", PathOf(entries, "adr-index"));
    }

    [Fact]
    public void ADeclaredTechnologyGetsItsOwnProperty()
    {
        IReadOnlyList<ConfigRegEntry> entries = ConfigRegConfig.ProductDefaults.Resolve(Ontology("dotnet", "github-actions"));

        Assert.Equal("docs/standards/dotnet/dotnet.standards.md", PathOf(entries, "dotnet-coding-standard"));
        Assert.Equal("docs/standards/github-actions/github-actions.standards.md", PathOf(entries, "github-actions-coding-standard"));
    }

    /// <summary>
    /// The docs root is the input to every derived path, so moving it moves the registry
    /// with it. This is why the entries are derived rather than stored: a stored copy would
    /// still name the old tree, and the operator would be repairing values they never wrote.
    /// </summary>
    [Fact]
    public void MovingTheDocsRootMovesEveryDerivedEntry()
    {
        IReadOnlyList<ConfigRegEntry> entries = ConfigRegConfig.ProductDefaults
            .Resolve(new OntologyConfig { DocsRoots = ["handbook"], Technologies = ["dotnet"] });

        Assert.All(entries, e => Assert.StartsWith("handbook", e.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void AHostAdditionReplacesABuiltInOfTheSameNameInPlace()
    {
        ConfigRegConfig config = new ConfigRegConfig
        {
            Additions = [new ConfigRegEntry("adr-index", "decisions/README.md")]
        };

        IReadOnlyList<ConfigRegEntry> entries = config.Resolve(Ontology());

        Assert.Equal("decisions/README.md", PathOf(entries, "adr-index"));
        Assert.Single(entries, e => e.Name == "adr-index");
    }

    [Fact]
    public void AHostAdditionWithANewNameIsAppended()
    {
        ConfigRegConfig config = new ConfigRegConfig
        {
            Additions = [new ConfigRegEntry("auth-design", "docs/reference/auth-design.md")]
        };

        Assert.Equal("docs/reference/auth-design.md", PathOf(config.Resolve(Ontology()), "auth-design"));
    }

    /// <summary>
    /// A mode property is a registry entry whose value is a setting rather than a path. The intake
    /// assessment is one: a portable skill asks the registry whether this repository opted in, and
    /// the rendered value <c>enabled</c> is the only thing that distinguishes an opted-in repository
    /// from one that never set the flag.
    /// </summary>
    [Fact]
    public void AnEnabledIntakeSettingPublishesTheIntakeAssessmentModeProperty()
    {
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml("ontology:\n  intake: true\n");
        IReadOnlyList<ConfigRegEntry> entries = config.ConfigReg.Resolve(config.Ontology);

        Assert.Contains("intake-assessment", entries.Select(entry => entry.Name));
        Assert.Contains(
            "- **<intake-assessment>**: `enabled`",
            ConfigRegMarkdown.Render(entries),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Adopting the intake assessment is opt-in, so a repository that never set the flag — or set it
    /// to false — publishes no mode property at all. Rendering one anyway would claim a behaviour the
    /// repository did not ask for, and the skills reading it would run an assessment nobody wanted.
    /// </summary>
    [Fact]
    public void TheIntakeAssessmentPropertyIsAbsentWhenTheSettingIsOff()
    {
        Assert.DoesNotContain(
            "intake-assessment",
            KyberWeaveConfigDefaults.Product.ConfigReg
                .Resolve(KyberWeaveConfigDefaults.Product.Ontology)
                .Select(entry => entry.Name));
        Assert.DoesNotContain("intake-assessment", Names(Resolved("ontology:\n  intake: false\n")));
        Assert.DoesNotContain("intake-assessment", Names(Resolved("ontology:\n  docs-root: docs\n")));
    }

    /// <summary>
    /// The registry as the operator's YAML resolves it. Loading configuration is the only path to
    /// the setting, so every assertion here starts from the file the operator writes.
    /// </summary>
    private static IReadOnlyList<ConfigRegEntry> Resolved(string yaml)
    {
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml(yaml);
        return config.ConfigReg.Resolve(config.Ontology);
    }

    private static IEnumerable<string> Names(IReadOnlyList<ConfigRegEntry> entries) =>
        entries.Select(entry => entry.Name);
}
