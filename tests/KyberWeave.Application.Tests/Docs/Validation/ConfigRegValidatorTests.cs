using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using Xunit;
using YamlDotNet.Core;
using KyberWeave.Tests;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>
/// Adoption is what makes a registry finding meaningful. Every corpus predating the registry
/// would otherwise fail the moment its CLI was upgraded, with an error per property, for a
/// structure it never asked for.
/// </summary>
public sealed class ConfigRegValidatorTests
{
    private readonly MemoryScaffoldedRepository _repo = new("config-reg");

    private KyberWeaveConfig Configured(params string[] technologies) =>
        KyberWeaveConfigDefaults.Product.WithOntology(
            new OntologyConfig { DocsRoots = ["docs"], Technologies = technologies });

    private void Write(string relativePath, string content) => _repo.Write(relativePath, content);

    private DiagnosticReport Validate(KyberWeaveConfig config) =>
        new ConfigRegValidator(_repo.Root, config, _repo.Store, _repo.Paths, _repo.Environment).Validate();

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
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");
        _repo.Memory.DeleteTreeNoFollow(Path.Combine(_repo.Root, "docs", "adr"));

        DiagnosticReport report = Validate(Configured());

        Assert.Contains(report.Items, i =>
            i.Code == ConfigRegValidator.UnresolvedPath && i.Message.Contains("adr-index", StringComparison.Ordinal));
    }

    /// <summary>
    /// A host that still holds its standard as <c>README.md</c> is in the warning period: the
    /// registry entry points at the prefixed name that does not exist yet, and that must not
    /// fail the host's own runs. The warning names the one command that finishes the migration.
    /// </summary>
    [Fact]
    public void ALegacyReadmeStandardIsAWarningNamingTheMigrationCommand()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");
        KyberWeaveConfig config = Configured("dotnet");
        string agents = Path.Combine(_repo.Root, "AGENTS.md");
        Write("AGENTS.md", ConfigRegMarkdown.Splice(
            _repo.Memory.ReadAllText(agents),
            ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology))));
        Write("docs/standards/dotnet/README.md", """
            ---
            id: standards/dotnet
            title: dotnet standard
            doc-type: coding-standard
            status: current
            technology: dotnet
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---

            # dotnet standard

            ## Stack
            """);

        DiagnosticReport report = Validate(config);

        Assert.False(report.HasErrors);
        Diagnostic warning = Assert.Single(
            report.Items, i => i.Code == ConfigRegValidator.UnresolvedPath);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("README.md", warning.Message, StringComparison.Ordinal);
        Assert.Contains("docs standards migrate", warning.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshlyScaffoldedRepositoryHasNoRegistryFindings()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");

        DiagnosticReport report = Validate(Configured());

        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
    }

    /// <summary>
    /// The prose between the property lines is template text, not configuration. A block whose
    /// properties all match is current even after its prose was edited, so the comparison must
    /// not fall through to a contradiction finding on the text alone.
    /// </summary>
    [Fact]
    public void ProseInsideTheGeneratedBlockIsNotChecked()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");
        string agents = Path.Combine(_repo.Root, "AGENTS.md");
        string original = _repo.Memory.ReadAllText(agents);
        string edited = original.Replace(
            "Agents and skills look up the following properties",
            "Agents and skills find the following properties",
            StringComparison.Ordinal);
        Assert.NotEqual(original, edited);
        Write("AGENTS.md", edited);

        DiagnosticReport report = Validate(Configured());

        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
    }

    [Fact]
    public void ARenderedBlockThatNoLongerMatchesConfigurationIsCONFIGREG002()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");

        // The repository declared a technology after the block was rendered: configuration
        // now publishes a property the file does not carry.
        DiagnosticReport report = Validate(Configured("dotnet"));

        Assert.Contains(report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
    }

    [Fact]
    public void AnAutonomyValueIsNotPathCheckedAsARegistryProperty()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");
        KyberWeaveConfig config = Configured().WithConfigReg(new ConfigRegConfig { Autonomy = "autonomous" });
        Write("AGENTS.md", ConfigRegMarkdown.Splice(
            _repo.Memory.ReadAllText(Path.Combine(_repo.Root, "AGENTS.md")),
            ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology), "autonomous")));

        DiagnosticReport report = Validate(config);

        Assert.Empty(report.Items);
    }

    [Fact]
    public void AMissingAutonomyLineIsCONFIGREG002()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");

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

    /// <summary>
    /// An onboarding value that was chosen publishes as a mode property, the way <c>autonomy</c> does, and the
    /// validator accepts the block that carries it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AGENTS.md</c>'s Config Reg block is the only place a portable agent can read a setting, so a repository
    /// that has onboarded has to say so there: a run-start check the worker cannot read is one it cannot honour.
    /// The value is a mode word, not a path, so it must not be path-checked — there is no <c>done</c> file, and
    /// reporting one would make the repository fail its own gate for having onboarded.
    /// </para>
    /// <para>
    /// The setting is loaded from YAML rather than set on a type on purpose: the contract is that an operator's
    /// <c>kyber-weave.yml</c> reaches the rendered registry, which is the only part a portable skill can see.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnOnboardingSettingPublishesAModePropertyTheValidatorAccepts()
    {
        Write(
            Path.Combine(".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\n  technologies:\n    - csharp\nonboarding: done\n");

        _repo.Scaffolding.Scaffold(_repo.Root);

        string agents = _repo.Memory.ReadAllText(Path.Combine(_repo.Root, "AGENTS.md"));
        KyberWeaveConfig config = _repo.Config.Load(_repo.Root);
        DiagnosticReport report = Validate(config);

        Assert.Contains("- **<onboarding>**: `done`", agents, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.UnresolvedPath);
        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
    }

    /// <summary>
    /// A repository that set no onboarding value publishes no line for it.
    /// </summary>
    /// <remarks>
    /// Absence is the setting's default — "not done" — and a property that published a default would tell a
    /// reader the repository had answered when it never did. This case passes before the setting exists and
    /// guards the one direction an implementation is most likely to get wrong by publishing
    /// <c>onboarding: none</c> or an empty value.
    /// </remarks>
    [Fact]
    public void AnUnsetOnboardingSettingPublishesNoRegistryLine()
    {
        Write(
            Path.Combine(".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\n  technologies:\n    - csharp\n");

        _repo.Scaffolding.Scaffold(_repo.Root);

        Assert.DoesNotContain(
            "onboarding",
            _repo.Memory.ReadAllText(Path.Combine(_repo.Root, "AGENTS.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnclosedMarkerIsReportedRatherThanOverwritten()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");
        string agents = Path.Combine(_repo.Root, "AGENTS.md");
        _repo.Memory.WriteAllText(agents,
            $"# Working here\n\n{ConfigRegMarkdown.StartMarker}\n- **<docs-root>**: `docs`\n\n## Mine\n\nKeep me.\n");

        DiagnosticReport report = Validate(Configured());

        Assert.Contains(report.Items, i =>
            i.Code == ConfigRegValidator.StaleRendering && i.Message.Contains("never closes", StringComparison.Ordinal));
        Assert.Contains("Keep me.", _repo.Memory.ReadAllText(agents), StringComparison.Ordinal);
    }

    /// <summary>
    /// A mode property carries a value, not a path, so it must not be path-checked: there is no
    /// <c>enabled</c> file for <c>KW-CONFIG-REG-001</c> to find, and reporting one would make an
    /// opted-in repository fail its own gate for adopting the feature.
    /// </summary>
    [Fact]
    public void AnEnabledIntakeSettingRendersAModePropertyThatIsNotPathChecked()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");
        KyberWeaveConfig config = IntakeEnabled();
        Write("AGENTS.md", ConfigRegMarkdown.Splice(
            _repo.Memory.ReadAllText(Path.Combine(_repo.Root, "AGENTS.md")),
            ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology))));

        DiagnosticReport report = Validate(config);

        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.UnresolvedPath);
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
        Assert.Contains(
            "- **<intake-assessment>**: `enabled`",
            _repo.Memory.ReadAllText(Path.Combine(_repo.Root, "AGENTS.md")),
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
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");

        DiagnosticReport report = Validate(IntakeEnabled());

        Assert.Contains(report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.UnresolvedPath);
    }

    /// <summary>
    /// The roster is a published mode property. This block was generated before the host chose the tiered roster,
    /// so it carries no <c>worker-roster</c> line while the configuration now publishes one. The finding must name
    /// the property and the command that fixes it, because the block is generated output.
    /// </summary>
    [Fact]
    public void AChangedWorkerRosterMakesTheScaffoldedBlockStaleAndNamesDocsInit()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");

        DiagnosticReport report = Validate(Tiered());

        Assert.Contains(report.Items, i =>
            i.Code == ConfigRegValidator.StaleRendering
            && i.Hint is not null
            && i.Hint.Contains("docs init", StringComparison.Ordinal));
    }

    /// <summary>
    /// A roster the configuration actually chose is published, and the block regenerated from that configuration
    /// validates: the finding above is about staleness, not about the roster being unpublishable.
    /// </summary>
    [Fact]
    public void AChosenWorkerRosterIsPublishedAndValidates()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");
        KyberWeaveConfig config = Tiered();
        Write(
            "AGENTS.md",
            ConfigRegMarkdown.Splice(
                _repo.Memory.ReadAllText(Path.Combine(_repo.Root, "AGENTS.md")),
                ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology))));

        DiagnosticReport report = Validate(config);

        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
        Assert.Contains(
            "- **<worker-roster>**: `tiered`",
            _repo.Memory.ReadAllText(Path.Combine(_repo.Root, "AGENTS.md")),
            StringComparison.Ordinal);
    }

    /// <summary>The generated block states which file wins, so a reader of any host knows before a run starts.</summary>
    [Fact]
    public void TheGeneratedBlockStatesThatTheConfigurationOutranksIt()
    {
        string block = ConfigRegMarkdown.Render(
            ConfigRegConfig.ProductDefaults.Resolve(new OntologyConfig { DocsRoots = ["docs"] }));

        Assert.Contains("The configuration is the authority", block, StringComparison.Ordinal);
        Assert.Contains("docs init", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adding a mode property is not a licence to stop checking paths: a registry that published
    /// <c>&lt;intake-assessment&gt;</c> and nothing else would pass while every path property
    /// pointed at nothing.
    /// </summary>
    [Fact]
    public void PathValuedRegistryEntriesAreStillPathCheckedWhenIntakeIsEnabled()
    {
        _repo.Scaffolding.Scaffold(_repo.Root, "docs");
        _repo.Memory.DeleteTreeNoFollow(Path.Combine(_repo.Root, "docs", "adr"));

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
        KyberWeaveConfigAssembler.FromYaml("ontology:\n  docs-root: docs\n  intake: true\n");

    /// <summary>A host configuration that chose the tiered worker roster.</summary>
    private static KyberWeaveConfig Tiered() =>
        KyberWeaveConfigAssembler.FromYaml("ontology:\n  docs-root: docs\nworker-roster: tiered\n");
}
