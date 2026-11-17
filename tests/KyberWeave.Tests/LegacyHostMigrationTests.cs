using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

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
public class LegacyHostMigrationTests : IDisposable
{
    private static readonly string[] Technologies =
        ["azure", "csharp", "data-access-layer", "github-actions", "react", "sql", "test"];

    private readonly TempDirectory _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
        GC.SuppressFinalize(this);
    }

    private string PathOf(string relative) =>
        Path.Combine(_repo.Path, relative.Replace('/', Path.DirectorySeparatorChar));

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
        DocsTestCommands.Scaffolding.Scaffold(_repo.Path, "6-Docs");

        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        File.WriteAllText(
            configPath,
            HostConfigYaml.WithTechnologies(File.ReadAllText(configPath), Technologies));

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_repo.Path);
        string agents = PathOf("AGENTS.md");
        File.WriteAllText(
            agents,
            ConfigRegMarkdown.Splice(
                File.ReadAllText(agents),
                ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology))));

        return config;
    }

    private void WriteLegacyStandards()
    {
        foreach (string technology in Technologies)
        {
            string folder = PathOf($"6-Docs/standards/{technology}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "README.md"), LegacyStandard(technology));
        }
    }

    private DiagnosticReport ValidateDocuments(KyberWeaveConfig config) =>
        new DocSpecValidator(_repo.Path, config.Ontology)
            .Validate(new DocumentLoader(_repo.Path, config.Ontology).Load());

    private DiagnosticReport ValidateRegistry(KyberWeaveConfig config) =>
        new ConfigRegValidator(_repo.Path, config, DocsPorts.Files).Validate();

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

    [Fact]
    public void TheMigrationLeavesTheHostValid()
    {
        KyberWeaveConfig config = DeclareTechnologies();
        WriteLegacyStandards();

        StandardsFolderMigrationResult result =
            new StandardsFolderMigration(_repo.Path, config.Ontology).Run();

        foreach (string technology in Technologies)
        {
            string standard = File.ReadAllText(
                PathOf($"6-Docs/standards/{technology}/{technology}.standards.md"));
            Assert.Equal(DocsLayout.StandardHeadings, LevelTwoHeadings(standard));
            Assert.Contains("### Stack", standard, StringComparison.Ordinal);
            Assert.Contains("### Naming", standard, StringComparison.Ordinal);
            Assert.Contains(
                $"- **{technology}:** the toolchain this host uses.",
                standard,
                StringComparison.Ordinal);
            Assert.False(File.Exists(PathOf($"6-Docs/standards/{technology}/README.md")));
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
        new StandardsFolderMigration(_repo.Path, config.Ontology).Run();

        string once = File.ReadAllText(PathOf("6-Docs/standards/csharp/csharp.standards.md"));
        StandardsFolderMigrationResult second =
            new StandardsFolderMigration(_repo.Path, config.Ontology).Run();

        Assert.Empty(second.Changes);
        Assert.Empty(second.Moves);
        Assert.Equal(once, File.ReadAllText(PathOf("6-Docs/standards/csharp/csharp.standards.md")));
    }

    private static IReadOnlyList<string> LevelTwoHeadings(string text) =>
        [.. text.Split('\n')
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())];

    private static string Describe(DiagnosticReport report) =>
        string.Join("; ", report.Items.Select(i => $"{i.Severity} {i.Code} {i.Message}"));
}
