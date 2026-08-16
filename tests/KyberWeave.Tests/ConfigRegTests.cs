using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

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

        Assert.Equal("docs/standards/dotnet/README.md", PathOf(entries, "dotnet-coding-standard"));
        Assert.Equal("docs/standards/github-actions/README.md", PathOf(entries, "github-actions-coding-standard"));
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
}

/// <summary>
/// Registry and technology values become directory names and the lookup names skills type by
/// hand, so both are constrained where the operator wrote them rather than where something
/// later builds a path out of them.
/// </summary>
public class ConfigRegLoadingTests
{
    private static YamlException AssertInvalid(string yaml) =>
        Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml(yaml));

    [Fact]
    public void ReadsHostAdditionsInTheOrderTheyWereWritten()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            config-reg:
              auth-design: docs/reference/auth-design.md
              azure-naming-standard: docs/reference/azure-naming.md
            """);

        Assert.Equal(
            ["auth-design", "azure-naming-standard"],
            config.ConfigReg.Additions.Select(e => e.Name));
    }

    [Theory]
    [InlineData("Auth Design")]
    [InlineData("auth_design")]
    [InlineData("-auth")]
    public void RejectsAPropertyNameASkillCouldNotType(string name)
    {
        Assert.Contains("lookup name", AssertInvalid($"config-reg:\n  {name}: docs/x.md\n").Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../elsewhere/x.md")]
    [InlineData("/etc/passwd")]
    public void RejectsAPathThatLeavesTheRepository(string path)
    {
        Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml($"config-reg:\n  x: {path}\n"));
    }

    [Theory]
    [InlineData("GitHub Actions")]
    [InlineData("standards/dotnet")]
    [InlineData("dotnet--core")]
    public void RejectsATechnologyThatIsNotASlug(string technology)
    {
        Assert.Contains("slug", AssertInvalid($"ontology:\n  technologies:\n    - {technology}\n").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsADuplicateTechnology()
    {
        Assert.Contains("more than once",
            AssertInvalid("ontology:\n  technologies:\n    - dotnet\n    - dotnet\n").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoTechnologyIsDeclaredByDefault()
    {
        Assert.Empty(KyberWeaveConfig.ProductDefaults.Ontology.Technologies);
    }
}

/// <summary>
/// Adoption is what makes a registry finding meaningful. Every corpus predating the registry
/// would otherwise fail the moment its CLI was upgraded, with an error per property, for a
/// structure it never asked for.
/// </summary>
public sealed class ConfigRegValidatorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private KyberWeaveConfig Configured(params string[] technologies) =>
        KyberWeaveConfig.ProductDefaults.WithOntology(
            new OntologyConfig { DocsRoots = ["docs"], Technologies = technologies });

    private void Write(string relativePath, string content)
    {
        string full = Path.Combine(_temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private DiagnosticReport Validate(KyberWeaveConfig config) =>
        new ConfigRegValidator(_temp.Path, config).Validate();

    [Fact]
    public void ARepositoryThatNeverAdoptedTheRegistryIsSilent()
    {
        Write("AGENTS.md", "# Working here\n\nNothing generated in this file.\n");

        Assert.Empty(Validate(Configured()).Items);
    }

    [Fact]
    public void DeclaringAnAdditionAdoptsTheRegistry()
    {
        Write("AGENTS.md", "# Working here\n");
        KyberWeaveConfig config = Configured().WithConfigReg(new ConfigRegConfig
        {
            Additions = [new ConfigRegEntry("auth-design", "docs/reference/auth-design.md")]
        });

        Assert.Contains(Validate(config).Items, i => i.Code == ConfigRegValidator.StaleRendering);
    }

    [Fact]
    public void AnAdoptedRegistryPointingAtNothingIsCONFIGREG001()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");
        Directory.Delete(Path.Combine(_temp.Path, "docs", "adr"), recursive: true);

        DiagnosticReport report = Validate(Configured());

        Assert.Contains(report.Items, i =>
            i.Code == ConfigRegValidator.UnresolvedPath && i.Message.Contains("adr-index", StringComparison.Ordinal));
    }

    [Fact]
    public void AFreshlyScaffoldedRepositoryHasNoRegistryFindings()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");

        DiagnosticReport report = Validate(Configured());

        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
    }

    [Fact]
    public void ARenderedBlockThatNoLongerMatchesConfigurationIsCONFIGREG002()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");

        // The repository declared a technology after the block was rendered: configuration
        // now publishes a property the file does not carry.
        DiagnosticReport report = Validate(Configured("dotnet"));

        Assert.Contains(report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
    }

    [Fact]
    public void AnUnclosedMarkerIsReportedRatherThanOverwritten()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");
        string agents = Path.Combine(_temp.Path, "AGENTS.md");
        File.WriteAllText(agents,
            $"# Working here\n\n{ConfigRegMarkdown.StartMarker}\n- **<docs-root>**: `docs`\n\n## Mine\n\nKeep me.\n");

        DiagnosticReport report = Validate(Configured());

        Assert.Contains(report.Items, i =>
            i.Code == ConfigRegValidator.StaleRendering && i.Message.Contains("never closes", StringComparison.Ordinal));
        Assert.Contains("Keep me.", File.ReadAllText(agents), StringComparison.Ordinal);
    }
}
