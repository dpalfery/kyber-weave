using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Bootstrapping a host repository. The property that matters most is the last one: what
/// <c>docs init</c> emits must itself pass <c>docs validate</c>, or adoption starts with
/// the tool reporting failures against its own scaffolding.
/// </summary>
public sealed class DocsScaffolderTests : IDisposable
{
    private const string ConfigPath = ".kyber-weave/kyber-weave.yml";

    /// <summary>
    /// A config as an operator would actually keep one: comments, a moved docs root, and
    /// three kinds of override the scaffolder's own template never emits.
    /// </summary>
    private const string HandMaintainedConfig =
        """
        # Ours, not the tool's.
        ontology:
          docs-root: 6-Docs  # moved in 2024
          excluded-segments:
            - archive
          catalog:
            component-column: 3
            owner-column: 8
        harness:
          profiles:
            claude:
              directory-name: .agents/agents
        """;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(_temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private void WriteHostConfig(string yaml)
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".kyber-weave"));
        File.WriteAllText(Path.Combine(_temp.Path, ".kyber-weave", "kyber-weave.yml"), yaml);
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("6-Docs")]
    [InlineData("doc")]
    [InlineData("documentation")]
    public void DetectsAnExistingConventionalDocsRoot(string existing)
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, existing));

        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal(existing, result.DocsRoot);
        Assert.Equal(DocsRootSource.Convention, result.DocsRootSource);
    }

    [Fact]
    public void PrefersDocsWhenSeveralConventionalRootsExist()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "6-Docs"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));

        Assert.Equal("docs", DocsScaffolder.Scaffold(_temp.Path).DocsRoot);
    }

    /// <summary>
    /// Re-running <c>docs init</c> must honor the docs root an existing
    /// <c>.kyber-weave/kyber-weave.yml</c> declares. <c>docs validate</c> reads the root
    /// from there, so a re-run that re-detected by convention (<c>docs</c> ranks ahead of
    /// <c>6-Docs</c>) would scaffold into a different tree than validate then reads.
    /// </summary>
    [Fact]
    public void AnExistingConfigDocsRootWinsOverConventionalDetection()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_temp.Path, ".kyber-weave", "kyber-weave.yml"),
            """
            ontology:
              docs-root: 6-Docs
            """);

        // A docs/ directory would otherwise be detected first.
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));
        Directory.CreateDirectory(Path.Combine(_temp.Path, "6-Docs"));

        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("6-Docs", result.DocsRoot);
        Assert.Equal(DocsRootSource.Configuration, result.DocsRootSource);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "6-Docs", "catalog.md")));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "6-Docs", "documentation-ontology.md")));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "docs", "catalog.md")));
    }

    /// <summary>
    /// The root <c>docs init</c> resolves must match the root <c>docs validate</c> resolves,
    /// even when a config omits <c>docs-root</c> (validate falls back to the product
    /// default). A re-detected conventional root would diverge.
    /// </summary>
    [Fact]
    public void InitResolvesToTheSameRootValidateUsesWhenConfigOmitsDocsRoot()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_temp.Path, ".kyber-weave", "kyber-weave.yml"),
            """
            ontology:
              excluded-files: []
            """);

        // A docs/ directory would be detected by convention, but validate ignores it.
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));

        var result = DocsScaffolder.Scaffold(_temp.Path);
        var validateRoot = KyberWeaveConfigLoader.Load(_temp.Path).Ontology.DocsRoot;

        Assert.Equal(validateRoot, result.DocsRoot);
        Assert.Equal(DocsRootSource.Configuration, result.DocsRootSource);
    }

    [Fact]
    public void FallsBackToDocsWhenNoConventionalRootExists()
    {
        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("docs", result.DocsRoot);
        Assert.Equal(DocsRootSource.Convention, result.DocsRootSource);
        Assert.True(Directory.Exists(Path.Combine(_temp.Path, "docs")));
    }

    [Fact]
    public void AnExplicitDocsRootOverridesDetection()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));

        var result = DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        Assert.Equal("handbook", result.DocsRoot);
        Assert.Equal(DocsRootSource.Explicit, result.DocsRootSource);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "handbook", "catalog.md")));
    }

    [Fact]
    public void TheEmittedConfigPointsAtTheResolvedDocsRoot()
    {
        DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        var config = KyberWeaveConfigLoader.Load(_temp.Path);

        Assert.Equal("handbook", config.Ontology.DocsRoot);
    }

    /// <summary>
    /// The product defaults carry five DevOps exclusions from the repository the ontology
    /// was first built for. A fresh host must not inherit them.
    /// </summary>
    [Fact]
    public void TheEmittedConfigClearsInheritedFileExclusions()
    {
        DocsScaffolder.Scaffold(_temp.Path);

        var config = KyberWeaveConfigLoader.Load(_temp.Path);

        Assert.Empty(config.Ontology.ExcludedFiles);
    }

    [Fact]
    public void ExistingFilesAreLeftAloneAndReportedAsSuch()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));
        File.WriteAllText(Path.Combine(_temp.Path, "docs", "catalog.md"), "hand written");

        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("hand written", Read("docs/catalog.md"));
        var catalog = result.Files.Single(f => f.RelativePath == "docs/catalog.md");
        Assert.Equal(ScaffoldOutcome.Skipped, catalog.Outcome);
        Assert.Null(catalog.Note);
        Assert.False(catalog.Written);
        Assert.True(result.Files.Single(f => f.RelativePath == "docs/documentation-ontology.md").Written);
    }

    [Fact]
    public void ForceOverwritesExistingFiles()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));
        File.WriteAllText(Path.Combine(_temp.Path, "docs", "catalog.md"), "hand written");

        var result = DocsScaffolder.Scaffold(_temp.Path, force: true);

        Assert.NotEqual("hand written", Read("docs/catalog.md"));
        Assert.True(result.Files.Single(f => f.RelativePath == "docs/catalog.md").Written);
    }

    /// <summary>
    /// A host config carries settings this scaffolder's template knows nothing about —
    /// harness profiles, catalog column overrides, closed vocabularies. Regenerating it
    /// under <c>--force</c> would discard all of them to restate a docs root the file
    /// already had.
    /// </summary>
    [Fact]
    public void ForceDoesNotOverwriteTheHostConfig()
    {
        WriteHostConfig(HandMaintainedConfig);

        var result = DocsScaffolder.Scaffold(_temp.Path, force: true);

        Assert.Equal(HandMaintainedConfig, Read(ConfigPath));

        var entry = result.Files.Single(f => f.RelativePath == ConfigPath);
        Assert.Equal(ScaffoldOutcome.Preserved, entry.Outcome);
        Assert.Equal("your configuration, kept as-is; --force does not overwrite it", entry.Note);
        Assert.False(entry.Written);
    }

    /// <summary>
    /// Moving the docs root is the one config change <c>docs init</c> owns — leaving it
    /// stale would point <c>docs validate</c> at a different tree than the catalog was just
    /// written to. Everything else in the file, including comments and keys the scaffolder
    /// never emits, has to survive the edit.
    /// </summary>
    [Fact]
    public void AnExplicitDocsRootRewritesThatKeyAndNothingElse()
    {
        WriteHostConfig(HandMaintainedConfig);

        var result = DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook", force: true);

        var config = KyberWeaveConfigLoader.Load(_temp.Path);
        Assert.Equal("handbook", config.Ontology.DocsRoot);

        // Every other override the operator wrote is still in force.
        Assert.Equal(3, config.Ontology.CatalogComponentColumn);
        Assert.Equal(8, config.Ontology.CatalogOwnerColumn);
        Assert.Equal(["archive"], config.Ontology.ExcludedPathSegments);
        Assert.Equal(".agents/agents", config.Harness.Profiles[HarnessKind.Claude].DirectoryName);

        // Verbatim, not merely semantically: comments and key order are the operator's.
        var text = Read(ConfigPath);
        Assert.Equal(
            HandMaintainedConfig.Replace(
                "docs-root: 6-Docs  # moved in 2024", "docs-root: 'handbook'  # moved in 2024",
                StringComparison.Ordinal),
            text);

        Assert.Equal(ScaffoldOutcome.Updated, result.Files.Single(f => f.RelativePath == ConfigPath).Outcome);
    }

    /// <summary>
    /// A re-run that resolves the root out of the config must not rewrite the file it just
    /// read: an unchanged docs root is not a reason to touch operator state.
    /// </summary>
    [Fact]
    public void AnUnchangedDocsRootLeavesTheConfigByteForByteIdentical()
    {
        WriteHostConfig(HandMaintainedConfig);

        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal(HandMaintainedConfig, Read(ConfigPath));
        Assert.Equal(ScaffoldOutcome.Preserved, result.Files.Single(f => f.RelativePath == ConfigPath).Outcome);
    }

    /// <summary>
    /// A config with no <c>docs-root</c> gains one rather than being regenerated, and the
    /// keys around it stay put.
    /// </summary>
    [Fact]
    public void AConfigWithoutADocsRootGainsTheKeyInPlace()
    {
        WriteHostConfig(
            """
            ontology:
              excluded-segments:
                - archive
            """);

        DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        var config = KyberWeaveConfigLoader.Load(_temp.Path);
        Assert.Equal("handbook", config.Ontology.DocsRoot);
        Assert.Equal(["archive"], config.Ontology.ExcludedPathSegments);
    }

    /// <summary>
    /// Only a direct child of <c>ontology</c> is the configured docs root. An unknown
    /// nested mapping may legitimately use the same key name and must remain operator state.
    /// </summary>
    [Fact]
    public void ANestedDocsRootIsNotMistakenForTheOntologyDocsRoot()
    {
        WriteHostConfig(
            """
            ontology:
              extension:
                docs-root: extension-docs
            """);

        DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        var text = Read(ConfigPath);
        Assert.Contains("\n  docs-root: 'handbook'\n", text, StringComparison.Ordinal);
        Assert.Contains("\n    docs-root: extension-docs", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Comment indentation is presentation only. It must not determine the indentation of
    /// an inserted key when direct ontology children already establish the block depth.
    /// </summary>
    [Fact]
    public void AnInsertedDocsRootUsesTheShallowestContentIndent()
    {
        WriteHostConfig(
            """
            ontology:
                # Deliberately deeper than the keys around it.
              excluded-files: []
            """);

        DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        var text = Read(ConfigPath);
        Assert.Contains("\n  docs-root: 'handbook'\n", text, StringComparison.Ordinal);
        Assert.Equal("handbook", KyberWeaveConfigLoader.Load(_temp.Path).Ontology.DocsRoot);
    }

    /// <summary>
    /// A config with no <c>ontology:</c> block at all — harness settings only — gains one
    /// without losing the block it does have.
    /// </summary>
    [Fact]
    public void AConfigWithoutAnOntologyBlockGainsOne()
    {
        WriteHostConfig(
            """
            harness:
              profiles:
                claude:
                  directory-name: .agents/agents
            """);

        DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        var config = KyberWeaveConfigLoader.Load(_temp.Path);
        Assert.Equal("handbook", config.Ontology.DocsRoot);
        Assert.Equal(".agents/agents", config.Harness.Profiles[HarnessKind.Claude].DirectoryName);
    }

    /// <summary>
    /// Hosts configured through the legacy repo-root <c>kyber-weave.yml</c> are read from
    /// that file. Creating <c>.kyber-weave/kyber-weave.yml</c> alongside it would not
    /// overwrite it, but would shadow it — the same loss by a different route.
    /// </summary>
    [Fact]
    public void TheLegacyRootConfigIsUpdatedRatherThanShadowed()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "kyber-weave.yml"), HandMaintainedConfig);

        var result = DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook", force: true);

        Assert.False(Directory.Exists(Path.Combine(_temp.Path, ".kyber-weave")));

        var config = KyberWeaveConfigLoader.Load(_temp.Path);
        Assert.Equal("handbook", config.Ontology.DocsRoot);
        Assert.Equal(3, config.Ontology.CatalogComponentColumn);
        Assert.Contains(result.Files, f => f.RelativePath == "kyber-weave.yml");
    }

    /// <summary>
    /// Every <c>KW-DOC-SPEC-001</c> diagnostic tells the author to read
    /// <c>&lt;docs-root&gt;/documentation-ontology.md</c>. Before <c>docs init</c> existed
    /// that hint pointed at a file the tool never produced.
    /// </summary>
    [Fact]
    public void TheOntologyReferenceTheDiagnosticHintNamesIsEmitted()
    {
        var result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.True(File.Exists(
            Path.Combine(_temp.Path, result.DocsRoot, "documentation-ontology.md")));
    }

    [Fact]
    public void TheSeededCatalogSuppliesTheOwnerTheScaffoldedDocumentsClaim()
    {
        DocsScaffolder.Scaffold(_temp.Path, owner: "platform-team");

        var set = new DocumentLoader(_temp.Path, "docs").Load();

        Assert.Contains("platform-team", set.Owners);
        Assert.All(set.Documents, d => Assert.Equal("platform-team", d.Frontmatter.Owner));
    }

    /// <summary>
    /// <c>--docs-root</c> is operator-supplied and reaches <c>Path.Combine</c>, which
    /// returns a rooted second argument outright and happily walks upward through '..'.
    /// Unchecked, <c>docs init</c> writes its scaffolding anywhere the process can reach.
    /// </summary>
    [Theory]
    [InlineData("../escaped")]
    [InlineData("../../escaped")]
    [InlineData("docs/../../escaped")]
    public void RefusesADocsRootThatEscapesTheRepositoryRoot(string escaping)
    {
        var error = Assert.Throws<ArgumentException>(
            () => DocsScaffolder.Scaffold(_temp.Path, escaping));

        Assert.Contains("outside the repository root", error.Message, StringComparison.Ordinal);

        // Nothing at all, not merely no documents: the host config resolves inside the
        // root, so a check that only fired per write would leave it behind pointing at a
        // docs root that was rejected a moment later.
        Assert.Empty(Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void RefusesAnAbsoluteDocsRoot()
    {
        using var elsewhere = new TempDirectory();

        Assert.Throws<ArgumentException>(
            () => DocsScaffolder.Scaffold(_temp.Path, Path.Combine(elsewhere.Path, "pwned")));

        Assert.Empty(Directory.GetFiles(elsewhere.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Windows resolves paths case-insensitively while Unix does not. Exercise both modes
    /// directly so the regression remains covered on the project's Unix CI runners.
    /// </summary>
    [Fact]
    public void ContainmentComparisonUsesTargetPlatformCasingRules()
    {
        const string boundary = @"C:\work\repo\";
        const string differentlyCasedChild = @"c:\WORK\REPO\docs\catalog.md";

        Assert.True(DocsScaffolder.IsWithinRepositoryBoundary(
            differentlyCasedChild, boundary, isWindows: true));
        Assert.False(DocsScaffolder.IsWithinRepositoryBoundary(
            differentlyCasedChild, boundary, isWindows: false));
    }

    /// <summary>
    /// <c>owner</c> lands in YAML frontmatter and in a pipe-delimited catalog row, so a
    /// newline would add a key and a pipe would shift the columns the component and owner
    /// vocabularies are parsed from.
    /// </summary>
    [Theory]
    [InlineData("platform\nstatus: current")]
    [InlineData("platform | Injected")]
    [InlineData("platform\r\nid: hijacked")]
    public void RefusesAnOwnerThatWouldInjectStructure(string owner)
    {
        Assert.Throws<ArgumentException>(
            () => DocsScaffolder.Scaffold(_temp.Path, owner: owner));

        Assert.Empty(Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void AcceptsOrdinaryOwnerAndDocsRootValues()
    {
        var result = DocsScaffolder.Scaffold(_temp.Path, "team-docs", "platform-team");

        Assert.Equal("team-docs", result.DocsRoot);
        Assert.All(result.Files, f => Assert.True(f.Written));
    }

    /// <summary>
    /// YAML punctuation belongs to the value when quoted. It must survive both config and
    /// document frontmatter parsing without being mistaken for a mapping or comment.
    /// </summary>
    [Fact]
    public void QuotesYamlSpecialCharactersWithoutChangingTheirValues()
    {
        const string docsRoot = "docs'root#manual";
        const string owner = "platform's: \"core\" #1";

        DocsScaffolder.Scaffold(_temp.Path, docsRoot, owner);

        var ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        var set = new DocumentLoader(_temp.Path, ontology).Load();
        Assert.Equal(docsRoot, ontology.DocsRoot);
        Assert.Contains(owner, set.Owners);
        Assert.All(set.Documents, document => Assert.Equal(owner, document.Frontmatter.Owner));
    }

    /// <summary>Owner whitespace is canonicalized once so YAML and catalog agree.</summary>
    [Fact]
    public void TrimsOwnerBeforeWritingFrontmatterAndCatalog()
    {
        DocsScaffolder.Scaffold(_temp.Path, owner: " platform ");

        var ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        var set = new DocumentLoader(_temp.Path, ontology).Load();
        var report = new DocSpecValidator(_temp.Path, ontology).Validate(set);
        Assert.Contains("platform", set.Owners);
        Assert.All(set.Documents, document => Assert.Equal("platform", document.Frontmatter.Owner));
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
    }

    /// <summary>Loaded config values pass the same pre-write validation as CLI values.</summary>
    [Fact]
    public void RefusesAControlCharacterDecodedFromExistingConfig()
    {
        const string yaml = "ontology:\n  docs-root: \"docs\\nstatus\"\n";
        WriteHostConfig(yaml);

        Assert.Throws<ArgumentException>(() => DocsScaffolder.Scaffold(_temp.Path));

        Assert.Equal(yaml, Read(ConfigPath));
        Assert.Single(Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>A Windows drive colon is data, not YAML structure.</summary>
    [Fact]
    public void QuotesAWindowsStyleDocsRoot()
    {
        const string docsRoot = "C:/work/repo/docs";
        var yaml = HostConfigYaml.WithDocsRoot("ontology:\n", docsRoot);
        WriteHostConfig(yaml);

        Assert.Contains("docs-root: 'C:/work/repo/docs'", yaml, StringComparison.Ordinal);
        Assert.Equal(docsRoot, KyberWeaveConfigLoader.Load(_temp.Path).Ontology.DocsRoot);
    }

    /// <summary>
    /// An unchanged setting is operator state. Plain, single-quoted, and double-quoted
    /// styles must all remain byte-for-byte identical when they already parse to the value.
    /// </summary>
    [Theory]
    [InlineData("docs")]
    [InlineData("'docs'")]
    [InlineData("\"docs\"")]
    public void PreservesAnEquivalentExistingDocsRootScalar(string token)
    {
        var yaml = $"ontology:\n  docs-root: {token}\n";

        Assert.Equal(yaml, HostConfigYaml.WithDocsRoot(yaml, "docs"));
    }

    /// <summary>A quoted hash belongs to the scalar; only the later hash opens a comment.</summary>
    [Fact]
    public void RewritesAQuotedDocsRootWithoutMistakingItsHashForAComment()
    {
        const string yaml = "ontology:\n  docs-root: 'old # root'  # keep this\n";

        Assert.Equal(
            "ontology:\n  docs-root: 'new # root'  # keep this\n",
            HostConfigYaml.WithDocsRoot(yaml, "new # root"));
    }

    /// <summary>The whole point: a freshly initialized repository validates clean.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("handbook")]
    public void AFreshlyScaffoldedCorpusPassesDocsValidate(string? docsRoot)
    {
        var result = DocsScaffolder.Scaffold(_temp.Path, docsRoot);

        var ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        var set = new DocumentLoader(_temp.Path, ontology).Load();
        var report = new DocSpecValidator(_temp.Path, ontology).Validate(set);

        Assert.Equal(2, set.Documents.Count);
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
        Assert.Equal(result.DocsRoot, ontology.DocsRoot);
    }
}
