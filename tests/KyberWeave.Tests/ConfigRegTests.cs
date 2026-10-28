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

    /// <summary>
    /// A mode property is a registry entry whose value is a setting rather than a path. The intake
    /// assessment is one: a portable skill asks the registry whether this repository opted in, and
    /// the rendered value <c>enabled</c> is the only thing that distinguishes an opted-in repository
    /// from one that never set the flag.
    /// </summary>
    [Fact]
    public void AnEnabledIntakeSettingPublishesTheIntakeAssessmentModeProperty()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("ontology:\n  intake: true\n");
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
            KyberWeaveConfig.ProductDefaults.ConfigReg
                .Resolve(KyberWeaveConfig.ProductDefaults.Ontology)
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
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml(yaml);
        return config.ConfigReg.Resolve(config.Ontology);
    }

    private static IEnumerable<string> Names(IReadOnlyList<ConfigRegEntry> entries) =>
        entries.Select(entry => entry.Name);
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
    [InlineData("supervised")]
    [InlineData("autonomous")]
    public void ReadsTheAutonomySetting(string value)
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml($"autonomy: {value}\n");

        Assert.Equal(value, config.ConfigReg.Autonomy);
    }

    [Fact]
    public void AutonomyIsUnsetByDefault()
    {
        Assert.Null(KyberWeaveConfig.ProductDefaults.ConfigReg.Autonomy);
        Assert.Null(KyberWeaveConfigLoader.LoadFromYaml("config-reg:\n  x: docs/x.md\n").ConfigReg.Autonomy);
    }

    [Theory]
    [InlineData("yolo")]
    [InlineData("Autonomous")]
    [InlineData("true")]
    public void RejectsAnAutonomyValueThatIsNotOneOfTheTwo(string value)
    {
        string message = AssertInvalid($"autonomy: {value}\n").Message;

        Assert.Contains("autonomy", message, StringComparison.Ordinal);
        Assert.Contains("supervised", message, StringComparison.Ordinal);
        Assert.Contains("autonomous", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AutonomyIsAReservedNameInTheConfigRegPathMap()
    {
        string message = AssertInvalid("config-reg:\n  autonomy: docs/x.md\n").Message;

        Assert.Contains("reserved", message, StringComparison.Ordinal);
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
    public void AnAutonomyValueIsNotPathCheckedAsARegistryProperty()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");
        KyberWeaveConfig config = Configured().WithConfigReg(new ConfigRegConfig { Autonomy = "autonomous" });
        Write("AGENTS.md", ConfigRegMarkdown.Splice(
            File.ReadAllText(Path.Combine(_temp.Path, "AGENTS.md")),
            ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology), "autonomous")));

        DiagnosticReport report = Validate(config);

        Assert.Empty(report.Items);
    }

    [Fact]
    public void AMissingAutonomyLineIsCONFIGREG002()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");

        DiagnosticReport report = Validate(
            Configured().WithConfigReg(new ConfigRegConfig { Autonomy = "autonomous" }));

        Assert.Contains(report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.UnresolvedPath);
    }

    [Fact]
    public void AWrongOrRemovedAutonomyLineIsCONFIGREG002()
    {
        KyberWeaveConfig autonomous = Configured().WithConfigReg(new ConfigRegConfig { Autonomy = "autonomous" });
        Write("AGENTS.md", ConfigRegMarkdown.Render(autonomous.ConfigReg.Resolve(autonomous.Ontology), "autonomous"));

        DiagnosticReport wrong = Validate(
            Configured().WithConfigReg(new ConfigRegConfig { Autonomy = "supervised" }));
        DiagnosticReport removed = Validate(Configured());

        Assert.Contains(wrong.Items, i => i.Code == ConfigRegValidator.StaleRendering);
        Assert.Contains(removed.Items, i => i.Code == ConfigRegValidator.StaleRendering);
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

    /// <summary>
    /// A mode property carries a value, not a path, so it must not be path-checked: there is no
    /// <c>enabled</c> file for <c>KW-CONFIG-REG-001</c> to find, and reporting one would make an
    /// opted-in repository fail its own gate for adopting the feature.
    /// </summary>
    [Fact]
    public void AnEnabledIntakeSettingRendersAModePropertyThatIsNotPathChecked()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");
        KyberWeaveConfig config = IntakeEnabled();
        Write("AGENTS.md", ConfigRegMarkdown.Splice(
            File.ReadAllText(Path.Combine(_temp.Path, "AGENTS.md")),
            ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology))));

        DiagnosticReport report = Validate(config);

        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.UnresolvedPath);
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
        Assert.Contains(
            "- **<intake-assessment>**: `enabled`",
            File.ReadAllText(Path.Combine(_temp.Path, "AGENTS.md")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The rendered block is generated output, so a repository that enabled the intake assessment
    /// after scaffolding carries a block that no longer matches configuration. The next
    /// <c>docs init</c> fixes it, which is what <c>KW-CONFIG-REG-002</c> asks for.
    /// </summary>
    [Fact]
    public void AnEnabledIntakeSettingMakesTheScaffoldedBlockStale()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");

        DiagnosticReport report = Validate(IntakeEnabled());

        Assert.Contains(report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.UnresolvedPath);
    }

    /// <summary>
    /// Adding a mode property is not a licence to stop checking paths: a registry that published
    /// <c>&lt;intake-assessment&gt;</c> and nothing else would pass while every path property
    /// pointed at nothing.
    /// </summary>
    [Fact]
    public void PathValuedRegistryEntriesAreStillPathCheckedWhenIntakeIsEnabled()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");
        Directory.Delete(Path.Combine(_temp.Path, "docs", "adr"), recursive: true);

        DiagnosticReport report = Validate(IntakeEnabled());

        Assert.Contains(report.Items, i =>
            i.Code == ConfigRegValidator.UnresolvedPath &&
            i.Message.Contains("adr-index", StringComparison.Ordinal));
    }

    /// <summary>
    /// A configured repository with the intake assessment enabled. Loaded from YAML because that is
    /// the setting's only surface: the point of the contract is that an operator's
    /// <c>kyber-weave.yml</c> reaches the rendered registry and the validator, not that a test can
    /// set a property on a type.
    /// </summary>
    private static KyberWeaveConfig IntakeEnabled() =>
        KyberWeaveConfigLoader.LoadFromYaml("ontology:\n  docs-root: docs\n  intake: true\n");
}
