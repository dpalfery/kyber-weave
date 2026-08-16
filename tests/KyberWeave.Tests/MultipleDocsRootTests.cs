using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A repository that documents components next to their code brings those files under
/// governance by naming each module as a documentation root. Before this, the only way in
/// was to relocate the file under the single <c>docs-root</c>.
/// </summary>
public class MultipleDocsRootTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- configuration --------------------------------------------------------------

    [Fact]
    public void AScalarDocsRootStillLoadsAsTheOneRoot()
    {
        OntologyConfig config = Load("""
            ontology:
              docs-root: handbook
            """);

        Assert.Equal(["handbook"], config.DocsRoots);
        Assert.Equal("handbook", config.DocsRoot);
    }

    [Fact]
    public void AListDocsRootLoadsEveryRootInTheOrderWritten()
    {
        OntologyConfig config = Load("""
            ontology:
              docs-root:
                - docs
                - automation
                - lab
            """);

        Assert.Equal(["docs", "automation", "lab"], config.DocsRoots);
    }

    /// <summary>Flow style is the same key, and hosts write it for short lists.</summary>
    [Fact]
    public void AFlowSequenceDocsRootLoadsTheSameWay()
    {
        OntologyConfig config = Load("ontology:\n  docs-root: [docs, automation]\n");

        Assert.Equal(["docs", "automation"], config.DocsRoots);
    }

    [Fact]
    public void TheFirstRootIsThePrimaryOne()
    {
        OntologyConfig config = Load("""
            ontology:
              docs-root:
                - automation
                - docs
            """);

        Assert.Equal("automation", config.DocsRoot);
        Assert.Equal("automation/catalog.md", config.ResolvedCatalogPath);
    }

    [Theory]
    [InlineData("docs/", "docs")]
    [InlineData("./docs", "docs")]
    [InlineData("docs\\nested", "docs/nested")]
    [InlineData(".", ".")]
    public void ARootIsCanonicalized(string written, string expected)
    {
        OntologyConfig config = Load($"ontology:\n  docs-root: '{written}'\n");

        Assert.Equal([expected], config.DocsRoots);
    }

    /// <summary>
    /// Two spellings of one directory are a redundancy, not a decision to second-guess —
    /// but the document beneath must still be loaded once.
    /// </summary>
    [Fact]
    public void DuplicateRootsCollapseRatherThanFailing()
    {
        OntologyConfig config = Load("""
            ontology:
              docs-root:
                - docs
                - docs/
                - automation
            """);

        Assert.Equal(["docs", "automation"], config.DocsRoots);
    }

    [Theory]
    [InlineData("../elsewhere")]
    [InlineData("docs/../../elsewhere")]
    public void ARootThatEscapesTheRepositoryIsAConfigurationError(string escaping)
    {
        OntologyConfigLoadResult result = TryLoad($"ontology:\n  docs-root: '{escaping}'\n");

        Assert.False(result.Success);
        Assert.Contains("escapes the repository root", result.ParseError, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsoluteRootIsAConfigurationError()
    {
        string absolute = OperatingSystem.IsWindows() ? "C:/elsewhere" : "/etc";

        OntologyConfigLoadResult result = TryLoad($"ontology:\n  docs-root: '{absolute}'\n");

        Assert.False(result.Success);
        Assert.Contains("is absolute", result.ParseError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bare slash collapses to empty after TrimEnd, and would otherwise be welcomed as
    /// the repository root. Rootedness has to be checked before that trim.
    /// </summary>
    [Fact]
    public void ARootThatIsOnlyASlashIsAbsolute()
    {
        OntologyConfigLoadResult result = TryLoad("ontology:\n  docs-root: '/'\n");

        Assert.False(result.Success);
        Assert.Contains("is absolute", result.ParseError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drive-letter forms are absolute on Windows; on Linux and macOS
    /// <c>Path.IsPathRooted</c> does not treat them as rooted, so they must still fail.
    /// </summary>
    [Fact]
    public void AWindowsDriveRootIsAbsoluteOnEveryHost()
    {
        OntologyConfigLoadResult result = TryLoad("ontology:\n  docs-root: 'C:/'\n");

        Assert.False(result.Success);
        Assert.Contains("is absolute", result.ParseError, StringComparison.Ordinal);
    }

    /// <summary>
    /// On a case-sensitive volume, <c>docs</c> and <c>Docs</c> are different directories.
    /// Collapsing them with OrdinalIgnoreCase would silently drop one.
    /// </summary>
    [Fact]
    public void CaseDistinctRootsSurviveOnCaseSensitiveHosts()
    {
        if (OperatingSystem.IsWindows()) return;

        OntologyConfig config = Load("""
            ontology:
              docs-root:
                - docs
                - Docs
            """);

        Assert.Equal(["docs", "Docs"], config.DocsRoots);
    }

    /// <summary>
    /// A mapping under the key is a typo, not a root. It must name the key rather than
    /// surface the deserializer's own type error.
    /// </summary>
    [Fact]
    public void ADocsRootThatIsNeitherADirectoryNorAListNamesTheKey()
    {
        OntologyConfigLoadResult result = TryLoad("""
            ontology:
              docs-root:
                primary: docs
            """);

        Assert.False(result.Success);
        Assert.Contains("ontology.docs-root", result.ParseError, StringComparison.Ordinal);
    }

    // --- scanning -------------------------------------------------------------------

    [Fact]
    public void ADocumentInAnyRootIsLoadedAndValidated()
    {
        WriteCatalog("docs/catalog.md");
        Write("automation/README.md", Document("automation/readme", owner: "Maintainers"));
        Write("lab/README.md", Document("lab/readme", owner: "not-in-the-catalog"));

        DiagnosticReport report = Validate(MultiRootConfig);

        Assert.Contains(report.Items, i =>
            i.FilePath == "lab/README.md" && i.Code == DocSpecValidator.UnknownCatalogValue);
        Assert.DoesNotContain(report.Items, i => i.FilePath == "automation/README.md");
    }

    [Fact]
    public void RootsAreWalkedInConfiguredOrder()
    {
        WriteCatalog("docs/catalog.md");
        Write("automation/README.md", Document("automation/readme"));
        Write("lab/README.md", Document("lab/readme"));

        List<string> paths = new DocumentLoader(_temp.Path, MultiRootConfig).Load()
            .Documents.Select(d => d.RelativePath).ToList();

        Assert.Equal(["docs/catalog.md", "automation/README.md", "lab/README.md"], paths);
    }

    /// <summary>
    /// A module root nested inside a wider one is a reasonable thing to configure. Loading
    /// the file twice would report every finding twice and fail its own id-uniqueness check.
    /// </summary>
    [Fact]
    public void AnOverlappingRootDoesNotLoadADocumentTwice()
    {
        WriteCatalog("docs/catalog.md");
        Write("docs/nested/guide.md", Document("nested/guide"));

        OntologyConfig config = OntologyConfig.ProductDefaults.WithDocsRoots(["docs", "docs/nested"]);
        IReadOnlyList<DocumentModel> documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.Single(documents, d => d.RelativePath == "docs/nested/guide.md");
        Assert.False(new DocSpecValidator(_temp.Path, config).Validate(
            new DocumentLoader(_temp.Path, config).Load()).HasErrors);
    }

    /// <summary>Exclusion entries are recorded relative to a root, not to the repository.</summary>
    [Fact]
    public void ExcludedFilesApplyWithinEachRoot()
    {
        WriteCatalog("docs/catalog.md");
        Write("docs/vendored/upstream.md", "# no frontmatter\n");
        Write("automation/vendored/upstream.md", "# no frontmatter either\n");

        OntologyConfig config = MultiRootConfig.Clone(excludedFiles: ["vendored/upstream.md"]);
        IReadOnlyList<DocumentModel> documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.DoesNotContain(documents, d => d.RelativePath.EndsWith("upstream.md", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLongestMatchingRootDecidesWhatAnExclusionIsRelativeTo()
    {
        WriteCatalog("docs/catalog.md");
        Write("docs/nested/skip.md", "# skipped\n");

        OntologyConfig config = OntologyConfig.ProductDefaults
            .WithDocsRoots(["docs", "docs/nested"])
            .Clone(excludedFiles: ["skip.md"]);
        IReadOnlyList<DocumentModel> documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.DoesNotContain(documents, d => d.RelativePath == "docs/nested/skip.md");
    }

    /// <summary>
    /// Ids are author-supplied and permanent, so two roots holding a same-named file is
    /// exactly where a collision appears. The existing uniqueness rule has to catch it.
    /// </summary>
    [Fact]
    public void TheSameIdInTwoRootsIsACollision()
    {
        WriteCatalog("docs/catalog.md");
        Write("automation/README.md", Document("readme"));
        Write("lab/README.md", Document("readme"));

        DiagnosticReport report = Validate(MultiRootConfig);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.BadReference &&
            i.Message.Contains("is declared by 2 documents", StringComparison.Ordinal));
    }

    // --- the catalog ----------------------------------------------------------------

    [Fact]
    public void TheCatalogComesFromThePrimaryRootByDefault()
    {
        WriteCatalog("docs/catalog.md", component: "Docs");
        WriteCatalog("automation/catalog.md", component: "Automation", id: "automation/catalog");
        Write("automation/thing.md", Component("automation/thing", "Automation"));

        DiagnosticReport report = Validate(MultiRootConfig);

        // 'Automation' exists only in the second root's table, which supplies nothing.
        Assert.Contains(report.Items, i =>
            i.FilePath == "automation/thing.md" && i.Code == DocSpecValidator.UnknownCatalogValue);
    }

    [Fact]
    public void AConfiguredCatalogPathSuppliesTheVocabulary()
    {
        WriteCatalog(".kyber-weave/catalog.md", component: "Automation");
        Write("automation/thing.md", Component("automation/thing", "Automation"));

        OntologyConfig config = MultiRootConfig.Clone(catalogPath: ".kyber-weave/catalog.md");

        Assert.False(Validate(config).HasErrors);
    }

    /// <summary>
    /// A catalog outside every root would otherwise trade its own frontmatter validation
    /// and its retrievability for that placement.
    /// </summary>
    [Fact]
    public void ACatalogOutsideEveryRootIsStillAGovernedDocument()
    {
        WriteCatalog(".kyber-weave/catalog.md", owner: "not-in-the-catalog");

        OntologyConfig config = MultiRootConfig.Clone(catalogPath: ".kyber-weave/catalog.md");
        IReadOnlyList<DocumentModel> documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.Contains(documents, d => d.RelativePath == ".kyber-weave/catalog.md");
        Assert.Contains(Validate(config).Items, i =>
            i.FilePath == ".kyber-weave/catalog.md" && i.Code == DocSpecValidator.UnknownCatalogValue);
    }

    [Fact]
    public void ACatalogInsideARootIsNotLoadedTwice()
    {
        WriteCatalog("docs/catalog.md");

        OntologyConfig config = MultiRootConfig.Clone(catalogPath: "docs/catalog.md");
        IReadOnlyList<DocumentModel> documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.Single(documents, d => d.RelativePath == "docs/catalog.md");
    }

    // --- the --docs-root option -----------------------------------------------------

    [Fact]
    public void AnUnsuppliedDocsRootOptionLeavesTheConfiguredRootsAlone()
    {
        WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");

        Assert.True(TryResolveOntology([], out OntologyConfig? ontology, out _));
        Assert.Equal(["docs", "automation"], ontology.DocsRoots);
    }

    /// <summary>
    /// The option carries no default of its own, so a value that happens to equal the
    /// product default is still an override rather than being silently discarded.
    /// </summary>
    [Fact]
    public void ARepeatedDocsRootOptionReplacesTheConfiguredRoots()
    {
        WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");

        Assert.True(TryResolveOntology(["lab", OntologyConfig.DefaultDocsRoot], out OntologyConfig? ontology, out _));
        Assert.Equal(["lab", OntologyConfig.DefaultDocsRoot], ontology.DocsRoots);
    }

    [Fact]
    public void ADocsRootOptionThatEscapesTheRepositoryReportsKWCONFIG001()
    {
        WriteHostConfig("ontology:\n  docs-root: docs\n");

        Assert.False(TryResolveOntology(["../elsewhere"], out _, out DiagnosticReport? report));
        Assert.Contains(report.Items, i =>
            i.Code == KyberWeaveConfigLoader.ConfigLoadErrorCode &&
            i.Message.Contains("escapes the repository root", StringComparison.Ordinal));
    }

    // --- docs init ------------------------------------------------------------------

    /// <summary>
    /// Rewriting the key's value against a list would leave the '- item' lines orphaned
    /// below a scalar: the config would still parse, and would mean something the operator
    /// never wrote.
    /// </summary>
    [Fact]
    public void ScaffoldingLeavesAMultiRootConfigByteForByteIdentical()
    {
        string configPath = WriteHostConfig("""
            ontology:
              docs-root:
                - docs      # the primary root
                - automation
              excluded-files: []
            """);
        string before = File.ReadAllText(configPath);

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("docs", result.DocsRoot);
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    [Fact]
    public void ScaffoldingIntoAListedRootLeavesTheConfigAlone()
    {
        string configPath = WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");
        string before = File.ReadAllText(configPath);

        DocsScaffolder.Scaffold(_temp.Path, "automation");

        Assert.Equal(before, File.ReadAllText(configPath));
    }

    /// <summary>
    /// Ordering a host's roots is a decision this command does not get to make for them.
    /// </summary>
    [Fact]
    public void ScaffoldingIntoAnUnlistedRootReportsRatherThanRewriting()
    {
        string configPath = WriteHostConfig("""
            ontology:
              docs-root:
                - docs
                - automation
            """);
        string before = File.ReadAllText(configPath);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => DocsScaffolder.Scaffold(_temp.Path, "handbook"));

        Assert.Contains("handbook", ex.Message, StringComparison.Ordinal);
        Assert.Contains("docs, automation", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    /// <summary>A key with no value at all is still a scalar the scaffolder may fill in.</summary>
    [Fact]
    public void AnEmptyDocsRootKeyIsFilledInRatherThanTreatedAsAList()
    {
        string configPath = WriteHostConfig("ontology:\n  docs-root:\n  excluded-files: []\n");

        DocsScaffolder.Scaffold(_temp.Path, "handbook");

        Assert.Equal("handbook", KyberWeaveConfigLoader.Load(_temp.Path).Ontology.DocsRoot);
        Assert.Contains("excluded-files: []", File.ReadAllText(configPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// A multi-line flow sequence is valid YAML. Parsing only the opening bracket would
    /// fall through to the scalar rewrite and orphan the continuation lines — the exact
    /// corruption multi-root scaffolding exists to prevent.
    /// </summary>
    [Fact]
    public void ScaffoldingLeavesAMultiLineFlowSequenceAlone()
    {
        string configPath = WriteHostConfig("""
            ontology:
              docs-root: [
                docs,
                automation
              ]
            """);
        string before = File.ReadAllText(configPath);

        DocsScaffolder.Scaffold(_temp.Path, "docs");

        Assert.Equal(before, File.ReadAllText(configPath));
    }

    [Fact]
    public void ScaffoldingIntoAnUnlistedRootOfAMultiLineFlowSequenceReports()
    {
        string configPath = WriteHostConfig("""
            ontology:
              docs-root: [
                docs,
                automation
              ]
            """);
        string before = File.ReadAllText(configPath);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => DocsScaffolder.Scaffold(_temp.Path, "handbook"));

        Assert.Contains("handbook", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    // --- helpers --------------------------------------------------------------------

    private static OntologyConfig MultiRootConfig =>
        OntologyConfig.ProductDefaults.WithDocsRoots(["docs", "automation", "lab"]);

    private OntologyConfig Load(string yaml) =>
        OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, WriteYaml(yaml));

    private OntologyConfigLoadResult TryLoad(string yaml) =>
        OntologyConfigLoader.TryLoad(WriteYaml(yaml));

    private string WriteYaml(string yaml)
    {
        string path = Path.Combine(_temp.Path, "ontology-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private bool TryResolveOntology(
        string[] docsRoots,
        out OntologyConfig ontology,
        out KyberWeave.Core.Diagnostics.DiagnosticReport report)
    {
        report = new KyberWeave.Core.Diagnostics.DiagnosticReport();
        DocsSettings settings = new DocsSettings { Path = _temp.Path, DocsRoots = docsRoots };
        return DocsCommandComposition.TryResolveOntology(settings, report, out ontology);
    }

    private string WriteHostConfig(string yaml)
    {
        string path = Path.Combine(_temp.Path, ".kyber-weave", "kyber-weave.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        return path;
    }

    private void Write(string relativePath, string content)
    {
        string full = Path.Combine(_temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteCatalog(
        string relativePath,
        string component = "Docs",
        string owner = "Maintainers",
        string id = "system/catalog")
    {
        Write(relativePath, $"""
            ---
            id: {id}
            title: Component Catalog
            doc-type: index
            status: current
            owner: {owner}
            last-reviewed: 2026-07-21
            ---

            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            | --- | --- | --- | --- | --- | --- | --- | --- |
            | {component} | Application | `src` | [README](x) | [docs](y) | Maintainers | 2026-07-21 | Current |
            """);
    }

    private static string Document(string id, string owner = "Maintainers") => $"""
        ---
        id: {id}
        title: A Thing
        doc-type: reference
        status: current
        owner: {owner}
        last-reviewed: 2026-07-21
        ---

        # A Thing
        """;

    private static string Component(string id, string component) => $"""
        ---
        id: {id}
        title: A Component
        doc-type: requirements
        status: current
        owner: Maintainers
        last-reviewed: 2026-07-21
        component: {component}
        ---

        # A Component
        """;

    private KyberWeave.Core.Diagnostics.DiagnosticReport Validate(OntologyConfig config) =>
        new DocSpecValidator(_temp.Path, config).Validate(
            new DocumentLoader(_temp.Path, config).Load());
}
