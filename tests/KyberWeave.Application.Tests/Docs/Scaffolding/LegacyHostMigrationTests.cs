using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
/// A host shaped like the one that stopped a conductor run: seven technologies, every standard
/// still named <c>README.md</c>, and each standard holding its own headings (<c>## Stack</c>,
/// <c>## Naming</c>, <c>## Commands</c>) rather than the six the ontology requires.
/// </summary>
/// <remarks>
/// The migration has two promises here. Before it runs the host validates without errors, only
/// warnings that name the command. After it runs the same host validates cleanly, because the
/// standards were renamed, the missing folder files were written, and each standard's own
/// sections were kept under the heading each fits. A second run changes nothing.
/// </remarks>
public class LegacyHostMigrationTests
{
    private static readonly string[] Technologies =
        ["azure", "csharp", "data-access-layer", "github-actions", "react", "sql", "test"];

    private readonly MemoryScaffoldHost _repo = new();

    private string PathOf(string relative) => _repo.FullPath(relative);

    private static string LegacyStandard(string scope) => $"""
        ---
        id: standards/{scope}
        title: {scope} standard
        doc-type: coding-standard
        status: current
        technology: {scope}
        owner: unassigned
        last-reviewed: 2026-11-14
        ---

        # {scope} standard

        How {scope} is written in this repository.

        ## Stack

        - **{scope}:** the toolchain this host uses.

        ## Naming

        Names in {scope} are descriptive.

        ## Commands

        Run `build-{scope}` before opening a pull request.
        """;

    /// <summary>
    /// Scaffolds the documentation tree, then declares the seven technologies and renders the
    /// registry block as a host would after adding them, leaving only the legacy standards to be
    /// written.
    /// </summary>
    private KyberWeaveConfig DeclareTechnologies()
    {
        _repo.Scaffold("6-Docs");

        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        _repo.Memory.WriteAllText(
            configPath,
            HostConfigYaml.WithTechnologies(_repo.Memory.ReadAllText(configPath), Technologies));

        KyberWeaveConfig config = _repo.LoadConfig();
        string agents = PathOf("AGENTS.md");
        _repo.Memory.WriteAllText(
            agents,
            ConfigRegMarkdown.Splice(
                _repo.Memory.ReadAllText(agents),
                ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology))));

        return config;
    }

    private void WriteLegacyStandards()
    {
        foreach (string technology in Technologies)
        {
            string folder = PathOf($"6-Docs/standards/{technology}");
            _repo.Memory.CreateDirectory(folder);
            _repo.Memory.WriteAllText(Path.Combine(folder, "README.md"), LegacyStandard(technology));
        }
    }

    /// <summary>
    /// The registry block an older build generated: each standard entry names the folder's
    /// <c>README.md</c>, and the generated prose stops at "on the next run." because the
    /// build had no sentence about configuration being the authority.
    /// </summary>
    private void WriteOlderBuildRegistryBlock(KyberWeaveConfig config)
    {
        string block = ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology));
        foreach (string technology in Technologies)
        {
            block = block.Replace(
                $"6-Docs/standards/{technology}/{technology}.standards.md",
                $"6-Docs/standards/{technology}/README.md",
                StringComparison.Ordinal);
        }

        block = block.Replace(
            "run. The configuration is the authority when the two disagree: follow it, and have\n" +
            "`docs init` regenerate this block, rather than stopping a run over the difference.",
            "run.",
            StringComparison.Ordinal);

        string agents = PathOf("AGENTS.md");
        _repo.Memory.WriteAllText(agents, ConfigRegMarkdown.Splice(_repo.Memory.ReadAllText(agents), block));
    }

    private DiagnosticReport ValidateDocuments(KyberWeaveConfig config) =>
        _repo.Validator(config.Ontology).Validate(_repo.Loader(config.Ontology).Load());

    private DiagnosticReport ValidateRegistry(KyberWeaveConfig config) =>
        _repo.RegistryValidator(config).Validate();

    [Fact]
    public void AnUnmigratedHostValidatesWithoutErrors()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();

        DiagnosticReport documents = ValidateDocuments(config);
        DiagnosticReport registry = ValidateRegistry(config);

        Assert.False(documents.HasErrors, Describe(documents));
        Assert.False(registry.HasErrors, Describe(registry));
        Assert.Equal(
            Technologies.Length,
            documents.Items.Count(i => i.Code == DocSpecValidator.MissingFolderFile));
        Assert.Equal(
            Technologies.Length,
            registry.Items.Count(i => i.Code == ConfigRegValidator.UnresolvedPath));
        Assert.All(
            documents.Items.Concat(registry.Items),
            finding => Assert.Contains(
                "docs standards migrate", finding.Hint, StringComparison.Ordinal));
    }

    /// <summary>
    /// The block this test writes was generated by an older build, so it names the seven
    /// standards as <c>README.md</c> and carries the older generated prose. That is the state a
    /// conductor run found on the Hotshot base, and it must warn rather than stop the run: the
    /// block is stale only because the project has not upgraded.
    /// </summary>
    [Fact]
    public void AnOlderBuildsRegistryBlockIsAWarningDuringTheUpgradePeriod()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();
        WriteOlderBuildRegistryBlock(config);

        DiagnosticReport report = ValidateRegistry(config);

        Assert.False(report.HasErrors, Describe(report));
        Diagnostic stale = Assert.Single(
            report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
        Assert.Equal(Severity.Warning, stale.Severity);
        Assert.Contains("README.md", stale.Message, StringComparison.Ordinal);
        Assert.Contains(
            DocsLayout.MigrateStandardsCommand,
            stale.Hint,
            StringComparison.Ordinal);
        Assert.Equal(
            Technologies.Length,
            report.Items.Count(i => i.Code == ConfigRegValidator.UnresolvedPath));
    }

    /// <summary>
    /// Only the legacy standard names are upgrade-period differences. A block that also names
    /// another value than configuration does — a moved index, or a standard file that is not the
    /// folder's <c>README.md</c> — still contradicts the configuration and stays an error.
    /// </summary>
    [Fact]
    public void ARegistryBlockWithAnyOtherContradictionStaysAnError()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();
        WriteOlderBuildRegistryBlock(config);

        string agents = PathOf("AGENTS.md");
        _repo.Memory.WriteAllText(
            agents,
            _repo.Memory.ReadAllText(agents).Replace(
                "- **<plan-index>**: `6-Docs/plans/README.md`",
                "- **<plan-index>**: `6-Docs/plans/gone.md`",
                StringComparison.Ordinal));

        DiagnosticReport movedIndex = ValidateRegistry(config);
        Assert.True(movedIndex.HasErrors);
        Assert.Contains(
            movedIndex.Items,
            i => i.Code == ConfigRegValidator.StaleRendering && i.Severity == Severity.Error);

        _repo.Memory.WriteAllText(
            agents,
            _repo.Memory.ReadAllText(agents).Replace(
                "- **<csharp-coding-standard>**: `6-Docs/standards/csharp/README.md`",
                "- **<csharp-coding-standard>**: `6-Docs/standards/csharp/old.md`",
                StringComparison.Ordinal));

        DiagnosticReport renamedStandard = ValidateRegistry(config);
        Assert.True(renamedStandard.HasErrors);
        Assert.Contains(
            renamedStandard.Items,
            i => i.Code == ConfigRegValidator.StaleRendering && i.Severity == Severity.Error);
    }

    /// <summary>
    /// The generated prose is not a setting. A host whose entries all match configuration has no
    /// contradiction to report even when the paragraph around them came from an older build: the
    /// block is generated output, and the next <c>docs init</c> refreshes it.
    /// </summary>
    [Fact]
    public void AnOlderBuildsGeneratedProseAloneIsNotAStaleRegistry()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();

        string agents = PathOf("AGENTS.md");
        _repo.Memory.WriteAllText(
            agents,
            _repo.Memory.ReadAllText(agents).Replace(
                "run. The configuration is the authority when the two disagree: follow it, and have\n" +
                "`docs init` regenerate this block, rather than stopping a run over the difference.",
                "run.",
                StringComparison.Ordinal));

        DiagnosticReport report = ValidateRegistry(config);

        Assert.DoesNotContain(report.Items, i => i.Code == ConfigRegValidator.StaleRendering);
    }

    [Fact]
    public void TheMigrationLeavesTheHostValid()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();

        StandardsFolderMigrationResult result =
            _repo.Migration(config).Run();

        foreach (string technology in Technologies)
        {
            string standard = _repo.Memory.ReadAllText(
                PathOf($"6-Docs/standards/{technology}/{technology}.standards.md"));
            Assert.Equal(DocsLayout.StandardHeadings, LevelTwoHeadings(standard));
            Assert.Contains("### Stack", standard, StringComparison.Ordinal);
            Assert.Contains("### Naming", standard, StringComparison.Ordinal);
            Assert.Contains(
                $"- **{technology}:** the toolchain this host uses.",
                standard,
                StringComparison.Ordinal);
            Assert.False(_repo.Memory.FileExists(PathOf($"6-Docs/standards/{technology}/README.md")));
        }

        Assert.Equal(Technologies.Length * 2, result.Moves.Count);

        DiagnosticReport documents = ValidateDocuments(config);
        DiagnosticReport registry = ValidateRegistry(config);
        Assert.Empty(documents.Items);
        Assert.Empty(registry.Items);
    }

    [Fact]
    public void MigratingTwiceChangesNothingTheSecondTime()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();
        _repo.Migration(config).Run();

        string once = _repo.Memory.ReadAllText(PathOf("6-Docs/standards/csharp/csharp.standards.md"));
        StandardsFolderMigrationResult second =
            _repo.Migration(config).Run();

        Assert.Empty(second.Changes);
        Assert.Empty(second.Moves);
        Assert.False(second.ConfigRegRefreshed);
        Assert.Equal(once, _repo.Memory.ReadAllText(PathOf("6-Docs/standards/csharp/csharp.standards.md")));
    }

    /// <summary>
    /// The defect this closes: a host whose standards were still <c>README.md</c> when its
    /// registry block was rendered. The migration renames those standards, so the block now
    /// publishes seven paths that no longer exist; without re-rendering it, <c>docs validate</c>
    /// reports them as errors and only <c>docs init</c> clears them. One command now clears the
    /// whole warning period: the standards, their six headings, the missing folder files, and
    /// the block.
    /// </summary>
    [Fact]
    public void TheMigrationRefreshesAnOlderBuildsRegistryBlockAndLeavesTheHostValid()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();
        WriteOlderBuildRegistryBlock(config);
        string userConfig = _repo.Memory.ReadAllText(PathOf(".kyber-weave/kyber-weave.yml"));

        StandardsFolderMigrationResult result = _repo.Migration(config).Run();

        Assert.True(result.ConfigRegRefreshed);

        DiagnosticReport documents = ValidateDocuments(config);
        DiagnosticReport registry = ValidateRegistry(config);
        Assert.Empty(documents.Items);
        Assert.Empty(registry.Items);
        Assert.DoesNotContain(documents.Items, i =>
            i.Code is DocSpecValidator.MissingFolderFile or DocSpecValidator.StandardHeadings);
        Assert.DoesNotContain(registry.Items, i =>
            i.Code is ConfigRegValidator.UnresolvedPath or ConfigRegValidator.StaleRendering);

        Assert.DoesNotContain(
            "6-Docs/standards/csharp/README.md",
            _repo.Memory.ReadAllText(PathOf("AGENTS.md")),
            StringComparison.Ordinal);
        Assert.Equal(userConfig, _repo.Memory.ReadAllText(PathOf(".kyber-weave/kyber-weave.yml")));
    }

    /// <summary>
    /// The refreshed block is the only thing outside a standards folder the migration touches,
    /// and a second run finds neither a standard to rename nor a block to re-render.
    /// </summary>
    [Fact]
    public void MigratingAnOlderBuildsHostTwiceChangesNothingTheSecondTime()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();
        WriteOlderBuildRegistryBlock(config);
        _repo.Migration(config).Run();
        string agents = _repo.Memory.ReadAllText(PathOf("AGENTS.md"));

        StandardsFolderMigrationResult second = _repo.Migration(config).Run();

        Assert.Empty(second.Changes);
        Assert.Empty(second.Moves);
        Assert.False(second.ConfigRegRefreshed);
        Assert.Equal(agents, _repo.Memory.ReadAllText(PathOf("AGENTS.md")));
    }

    /// <summary>
    /// A dry run says the block would be refreshed and leaves it stale, like every other change
    /// it reports.
    /// </summary>
    [Fact]
    public void ADryRunReportsTheRegistryRefreshWithoutWritingIt()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();
        WriteOlderBuildRegistryBlock(config);
        string agents = _repo.Memory.ReadAllText(PathOf("AGENTS.md"));

        StandardsFolderMigrationResult result = _repo.Migration(config).Run(dryRun: true);

        Assert.True(result.ConfigRegRefreshed);
        Assert.Equal(agents, _repo.Memory.ReadAllText(PathOf("AGENTS.md")));
    }

    private static IReadOnlyList<string> LevelTwoHeadings(string text) =>
        [.. text.Split('\n')
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())];

    private static string Describe(DiagnosticReport report) =>
        string.Join("; ", report.Items.Select(i => $"{i.Severity} {i.Code} {i.Message}"));
}
