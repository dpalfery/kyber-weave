using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
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
    public void A_Scalar_DocsRoot_Still_Loads_As_The_One_Root()
    {
        var config = Load("""
            ontology:
              docs-root: handbook
            """);

        Assert.Equal(["handbook"], config.DocsRoots);
        Assert.Equal("handbook", config.DocsRoot);
    }

    [Fact]
    public void A_List_DocsRoot_Loads_Every_Root_In_The_Order_Written()
    {
        var config = Load("""
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
    public void A_Flow_Sequence_DocsRoot_Loads_The_Same_Way()
    {
        var config = Load("ontology:\n  docs-root: [docs, automation]\n");

        Assert.Equal(["docs", "automation"], config.DocsRoots);
    }

    [Fact]
    public void The_First_Root_Is_The_Primary_One()
    {
        var config = Load("""
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
    public void A_Root_Is_Canonicalized(string written, string expected)
    {
        var config = Load($"ontology:\n  docs-root: '{written}'\n");

        Assert.Equal([expected], config.DocsRoots);
    }

    /// <summary>
    /// Two spellings of one directory are a redundancy, not a decision to second-guess —
    /// but the document beneath must still be loaded once.
    /// </summary>
    [Fact]
    public void Duplicate_Roots_Collapse_Rather_Than_Failing()
    {
        var config = Load("""
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
    public void A_Root_That_Escapes_The_Repository_Is_A_Configuration_Error(string escaping)
    {
        var result = TryLoad($"ontology:\n  docs-root: '{escaping}'\n");

        Assert.False(result.Success);
        Assert.Contains("escapes the repository root", result.ParseError, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Absolute_Root_Is_A_Configuration_Error()
    {
        var absolute = OperatingSystem.IsWindows() ? "C:/elsewhere" : "/etc";

        var result = TryLoad($"ontology:\n  docs-root: '{absolute}'\n");

        Assert.False(result.Success);
        Assert.Contains("is absolute", result.ParseError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A mapping under the key is a typo, not a root. It must name the key rather than
    /// surface the deserializer's own type error.
    /// </summary>
    [Fact]
    public void A_DocsRoot_That_Is_Neither_A_Directory_Nor_A_List_Names_The_Key()
    {
        var result = TryLoad("""
            ontology:
              docs-root:
                primary: docs
            """);

        Assert.False(result.Success);
        Assert.Contains("ontology.docs-root", result.ParseError, StringComparison.Ordinal);
    }

    // --- scanning -------------------------------------------------------------------

    [Fact]
    public void A_Document_In_Any_Root_Is_Loaded_And_Validated()
    {
        WriteCatalog("docs/catalog.md");
        Write("automation/README.md", Document("automation/readme", owner: "Maintainers"));
        Write("lab/README.md", Document("lab/readme", owner: "not-in-the-catalog"));

        var report = Validate(MultiRootConfig);

        Assert.Contains(report.Items, i =>
            i.FilePath == "lab/README.md" && i.Code == DocSpecValidator.UnknownCatalogValue);
        Assert.DoesNotContain(report.Items, i => i.FilePath == "automation/README.md");
    }

    [Fact]
    public void Roots_Are_Walked_In_Configured_Order()
    {
        WriteCatalog("docs/catalog.md");
        Write("automation/README.md", Document("automation/readme"));
        Write("lab/README.md", Document("lab/readme"));

        var paths = new DocumentLoader(_temp.Path, MultiRootConfig).Load()
            .Documents.Select(d => d.RelativePath).ToList();

        Assert.Equal(["docs/catalog.md", "automation/README.md", "lab/README.md"], paths);
    }

    /// <summary>
    /// A module root nested inside a wider one is a reasonable thing to configure. Loading
    /// the file twice would report every finding twice and fail its own id-uniqueness check.
    /// </summary>
    [Fact]
    public void An_Overlapping_Root_Does_Not_Load_A_Document_Twice()
    {
        WriteCatalog("docs/catalog.md");
        Write("docs/nested/guide.md", Document("nested/guide"));

        var config = OntologyConfig.ProductDefaults.WithDocsRoots(["docs", "docs/nested"]);
        var documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.Single(documents, d => d.RelativePath == "docs/nested/guide.md");
        Assert.False(new DocSpecValidator(_temp.Path, config).Validate(
            new DocumentLoader(_temp.Path, config).Load()).HasErrors);
    }

    /// <summary>Exclusion entries are recorded relative to a root, not to the repository.</summary>
    [Fact]
    public void Excluded_Files_Apply_Within_Each_Root()
    {
        WriteCatalog("docs/catalog.md");
        Write("docs/vendored/upstream.md", "# no frontmatter\n");
        Write("automation/vendored/upstream.md", "# no frontmatter either\n");

        var config = MultiRootConfig.Clone(excludedFiles: ["vendored/upstream.md"]);
        var documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.DoesNotContain(documents, d => d.RelativePath.EndsWith("upstream.md", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Longest_Matching_Root_Decides_What_An_Exclusion_Is_Relative_To()
    {
        WriteCatalog("docs/catalog.md");
        Write("docs/nested/skip.md", "# skipped\n");

        var config = OntologyConfig.ProductDefaults
            .WithDocsRoots(["docs", "docs/nested"])
            .Clone(excludedFiles: ["skip.md"]);
        var documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.DoesNotContain(documents, d => d.RelativePath == "docs/nested/skip.md");
    }

    /// <summary>
    /// Ids are author-supplied and permanent, so two roots holding a same-named file is
    /// exactly where a collision appears. The existing uniqueness rule has to catch it.
    /// </summary>
    [Fact]
    public void The_Same_Id_In_Two_Roots_Is_A_Collision()
    {
        WriteCatalog("docs/catalog.md");
        Write("automation/README.md", Document("readme"));
        Write("lab/README.md", Document("readme"));

        var report = Validate(MultiRootConfig);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.BadReference &&
            i.Message.Contains("is declared by 2 documents", StringComparison.Ordinal));
    }

    // --- the catalog ----------------------------------------------------------------

    [Fact]
    public void The_Catalog_Comes_From_The_Primary_Root_By_Default()
    {
        WriteCatalog("docs/catalog.md", component: "Docs");
        WriteCatalog("automation/catalog.md", component: "Automation", id: "automation/catalog");
        Write("automation/thing.md", Component("automation/thing", "Automation"));

        var report = Validate(MultiRootConfig);

        // 'Automation' exists only in the second root's table, which supplies nothing.
        Assert.Contains(report.Items, i =>
            i.FilePath == "automation/thing.md" && i.Code == DocSpecValidator.UnknownCatalogValue);
    }

    [Fact]
    public void A_Configured_CatalogPath_Supplies_The_Vocabulary()
    {
        WriteCatalog(".kyber-weave/catalog.md", component: "Automation");
        Write("automation/thing.md", Component("automation/thing", "Automation"));

        var config = MultiRootConfig.Clone(catalogPath: ".kyber-weave/catalog.md");

        Assert.False(Validate(config).HasErrors);
    }

    /// <summary>
    /// A catalog outside every root would otherwise trade its own frontmatter validation
    /// and its retrievability for that placement.
    /// </summary>
    [Fact]
    public void A_Catalog_Outside_Every_Root_Is_Still_A_Governed_Document()
    {
        WriteCatalog(".kyber-weave/catalog.md", owner: "not-in-the-catalog");

        var config = MultiRootConfig.Clone(catalogPath: ".kyber-weave/catalog.md");
        var documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.Contains(documents, d => d.RelativePath == ".kyber-weave/catalog.md");
        Assert.Contains(Validate(config).Items, i =>
            i.FilePath == ".kyber-weave/catalog.md" && i.Code == DocSpecValidator.UnknownCatalogValue);
    }

    [Fact]
    public void A_Catalog_Inside_A_Root_Is_Not_Loaded_Twice()
    {
        WriteCatalog("docs/catalog.md");

        var config = MultiRootConfig.Clone(catalogPath: "docs/catalog.md");
        var documents = new DocumentLoader(_temp.Path, config).Load().Documents;

        Assert.Single(documents, d => d.RelativePath == "docs/catalog.md");
    }

    // --- the --docs-root option -----------------------------------------------------

    [Fact]
    public void An_Unsupplied_DocsRoot_Option_Leaves_The_Configured_Roots_Alone()
    {
        WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");

        Assert.True(TryResolveOntology([], out var ontology, out _));
        Assert.Equal(["docs", "automation"], ontology.DocsRoots);
    }

    /// <summary>
    /// The option carries no default of its own, so a value that happens to equal the
    /// product default is still an override rather than being silently discarded.
    /// </summary>
    [Fact]
    public void A_Repeated_DocsRoot_Option_Replaces_The_Configured_Roots()
    {
        WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");

        Assert.True(TryResolveOntology(["lab", OntologyConfig.DefaultDocsRoot], out var ontology, out _));
        Assert.Equal(["lab", OntologyConfig.DefaultDocsRoot], ontology.DocsRoots);
    }

    [Fact]
    public void A_DocsRoot_Option_That_Escapes_The_Repository_Reports_KW_CONFIG_001()
    {
        WriteHostConfig("ontology:\n  docs-root: docs\n");

        Assert.False(TryResolveOntology(["../elsewhere"], out _, out var report));
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
    public void Scaffolding_Leaves_A_Multi_Root_Config_Byte_For_Byte_Identical()
    {
        var configPath = WriteHostConfig("""
            ontology:
              docs-root:
                - docs      # the primary root
                - automation
              excluded-files: []
            """);
        var before = File.ReadAllText(configPath);

        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("docs", result.DocsRoot);
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    [Fact]
    public void Scaffolding_Into_A_Listed_Root_Leaves_The_Config_Alone()
    {
        var configPath = WriteHostConfig("ontology:\n  docs-root: [docs, automation]\n");
        var before = File.ReadAllText(configPath);

        DocsScaffolder.Scaffold(_temp.Path, "automation");

        Assert.Equal(before, File.ReadAllText(configPath));
    }

    /// <summary>
    /// Ordering a host's roots is a decision this command does not get to make for them.
    /// </summary>
    [Fact]
    public void Scaffolding_Into_An_Unlisted_Root_Reports_Rather_Than_Rewriting()
    {
        var configPath = WriteHostConfig("""
            ontology:
              docs-root:
                - docs
                - automation
            """);
        var before = File.ReadAllText(configPath);

        var ex = Assert.Throws<InvalidDataException>(
            () => DocsScaffolder.Scaffold(_temp.Path, "handbook"));

        Assert.Contains("handbook", ex.Message, StringComparison.Ordinal);
        Assert.Contains("docs, automation", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(configPath));
    }

    /// <summary>A key with no value at all is still a scalar the scaffolder may fill in.</summary>
    [Fact]
    public void An_Empty_DocsRoot_Key_Is_Filled_In_Rather_Than_Treated_As_A_List()
    {
        var configPath = WriteHostConfig("ontology:\n  docs-root:\n  excluded-files: []\n");

        DocsScaffolder.Scaffold(_temp.Path, "handbook");

        Assert.Equal("handbook", KyberWeaveConfigLoader.Load(_temp.Path).Ontology.DocsRoot);
        Assert.Contains("excluded-files: []", File.ReadAllText(configPath), StringComparison.Ordinal);
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
        var path = Path.Combine(_temp.Path, "ontology-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private bool TryResolveOntology(
        string[] docsRoots,
        out OntologyConfig ontology,
        out KyberWeave.Core.Diagnostics.DiagnosticReport report)
    {
        report = new KyberWeave.Core.Diagnostics.DiagnosticReport();
        var settings = new DocsSettings { Path = _temp.Path, DocsRoots = docsRoots };
        return DocsCommandComposition.TryResolveOntology(settings, report, out ontology);
    }

    private string WriteHostConfig(string yaml)
    {
        var path = Path.Combine(_temp.Path, ".kyber-weave", "kyber-weave.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
        return path;
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
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
