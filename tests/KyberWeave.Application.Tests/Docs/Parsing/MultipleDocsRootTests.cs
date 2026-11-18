using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Parsing;

/// <summary>
/// A repository that documents components next to their code brings those files under
/// governance by naming each module as a documentation root. Before this, the only way in
/// was to relocate the file under the single <c>docs-root</c>.
/// </summary>
public sealed class MultipleDocsRootTests
{
    private readonly DocFixture _fixture = DocFixture.CreateInMemory();

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
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows paths are case-insensitive, so case-distinct roots cannot exist there.");

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
            i is { FilePath: "lab/README.md", Code: DocSpecValidator.UnknownCatalogValue });
        Assert.DoesNotContain(report.Items, i => i.FilePath == "automation/README.md");
    }

    [Fact]
    public void RootsAreWalkedInConfiguredOrder()
    {
        WriteCatalog("docs/catalog.md");
        Write("automation/README.md", Document("automation/readme"));
        Write("lab/README.md", Document("lab/readme"));

        List<string> paths = _fixture.Loader(MultiRootConfig).Load()
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
        IReadOnlyList<DocumentModel> documents = _fixture.Loader(config).Load().Documents;

        Assert.Single(documents, d => d.RelativePath == "docs/nested/guide.md");
        Assert.False(_fixture.Validate(config).HasErrors);
    }

    /// <summary>Exclusion entries are recorded relative to a root, not to the repository.</summary>
    [Fact]
    public void ExcludedFilesApplyWithinEachRoot()
    {
        WriteCatalog("docs/catalog.md");
        Write("docs/vendored/upstream.md", "# no frontmatter\n");
        Write("automation/vendored/upstream.md", "# no frontmatter either\n");

        OntologyConfig config = MultiRootConfig.Clone(excludedFiles: ["vendored/upstream.md"]);
        IReadOnlyList<DocumentModel> documents = _fixture.Loader(config).Load().Documents;

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
        IReadOnlyList<DocumentModel> documents = _fixture.Loader(config).Load().Documents;

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
            i is { FilePath: "automation/thing.md", Code: DocSpecValidator.UnknownCatalogValue });
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
        IReadOnlyList<DocumentModel> documents = _fixture.Loader(config).Load().Documents;

        Assert.Contains(documents, d => d.RelativePath == ".kyber-weave/catalog.md");
        Assert.Contains(Validate(config).Items, i =>
            i is { FilePath: ".kyber-weave/catalog.md", Code: DocSpecValidator.UnknownCatalogValue });
    }

    [Fact]
    public void ACatalogInsideARootIsNotLoadedTwice()
    {
        WriteCatalog("docs/catalog.md");

        OntologyConfig config = MultiRootConfig.Clone(catalogPath: "docs/catalog.md");
        IReadOnlyList<DocumentModel> documents = _fixture.Loader(config).Load().Documents;

        Assert.Single(documents, d => d.RelativePath == "docs/catalog.md");
    }

    // --- docs init ------------------------------------------------------------------

    /// <summary>
    /// Rewriting the key's value against a list would leave the '- item' lines orphaned
    /// below a scalar: the config would still parse, and would mean something the operator
    /// never wrote. The scaffold appends the <c>mcp:</c> declaration it finds missing and
    /// leaves the sequence, its comment, and its order exactly as they were.
    /// </summary>
    [Fact]
    public void ScaffoldingLeavesAMultiRootSequenceIntactAndAppendsOnlyTheDeclaration()
    {
        string configPath = WriteHostConfig("""
            ontology:
              docs-root:
                - docs      # the primary root
                - automation
              excluded-files: []
            """);
        string before = _fixture.Memory!.ReadAllText(configPath);

        ScaffoldResult result = _fixture.Scaffolder().Scaffold(_fixture.Root);

        Assert.Equal("docs", result.DocsRoot);
        Assert.Equal(before.TrimEnd('\r', '\n') + McpWiringFixture.AppendedDeclaration(), _fixture.Memory!.ReadAllText(configPath));
    }

    [Fact]
    public void ScaffoldingIntoAListedRootLeavesTheConfigOtherwiseAlone()
    {
        string configPath = WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");
        string before = _fixture.Memory!.ReadAllText(configPath);

        _fixture.Scaffolder().Scaffold(_fixture.Root, "automation");

        Assert.Equal(before.TrimEnd('\r', '\n') + McpWiringFixture.AppendedDeclaration(), _fixture.Memory!.ReadAllText(configPath));
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
        string before = _fixture.Memory!.ReadAllText(configPath);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => _fixture.Scaffolder().Scaffold(_fixture.Root, "handbook"));

        Assert.Contains("handbook", ex.Message, StringComparison.Ordinal);
        Assert.Contains("docs, automation", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, _fixture.Memory!.ReadAllText(configPath));
    }

    /// <summary>A key with no value at all is still a scalar the scaffolder may fill in.</summary>
    [Fact]
    public void AnEmptyDocsRootKeyIsFilledInRatherThanTreatedAsAList()
    {
        string configPath = WriteHostConfig("ontology:\n  docs-root:\n  excluded-files: []\n");

        _fixture.Scaffolder().Scaffold(_fixture.Root, "handbook");

        Assert.Equal("handbook", _fixture.Config().Load(_fixture.Root).Ontology.DocsRoot);
        Assert.Contains("excluded-files: []", _fixture.Memory!.ReadAllText(configPath), StringComparison.Ordinal);
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
        string before = _fixture.Memory!.ReadAllText(configPath);

        _fixture.Scaffolder().Scaffold(_fixture.Root, "docs");

        Assert.Equal(before.TrimEnd('\r', '\n') + McpWiringFixture.AppendedDeclaration(), _fixture.Memory!.ReadAllText(configPath));
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
        string before = _fixture.Memory!.ReadAllText(configPath);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => _fixture.Scaffolder().Scaffold(_fixture.Root, "handbook"));

        Assert.Contains("handbook", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, _fixture.Memory!.ReadAllText(configPath));
    }

    // --- helpers --------------------------------------------------------------------

    private static OntologyConfig MultiRootConfig =>
        OntologyConfig.ProductDefaults.WithDocsRoots(["docs", "automation", "lab"]);

    private OntologyConfig Load(string yaml) =>
        _fixture.LoadOntologyMerged(OntologyConfig.ProductDefaults, WriteYaml(yaml));

    private OntologyConfigLoadResult TryLoad(string yaml) =>
        _fixture.Config().TryLoadOntology(WriteYaml(yaml));

    private string WriteYaml(string yaml)
    {
        const string name = "ontology.yml";
        _fixture.Write(name, yaml);
        return Path.Combine(_fixture.Root, name);
    }

    private string WriteHostConfig(string yaml)
    {
        const string relative = ".kyber-weave/kyber-weave.yml";
        _fixture.Write(relative, yaml);
        return Path.Combine(_fixture.Root, ".kyber-weave", "kyber-weave.yml");
    }

    private void Write(string relativePath, string content)
    {
        _fixture.Write(relativePath, content);
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

    private DiagnosticReport Validate(OntologyConfig config) => _fixture.Validate(config);
}
