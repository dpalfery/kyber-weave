using System.Text;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using Xunit;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
/// Bootstrapping a host repository. The property that matters most is the last one: what
/// <c>docs init</c> emits must itself pass <c>docs validate</c>, or adoption starts with
/// the tool reporting failures against its own scaffolding.
/// </summary>
public sealed class DocsScaffolderTests
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

    private static readonly IKyberStandardsTemplates Templates = ShippedKyberStandards.Templates;

    private readonly MemoryScaffoldHost _host = new();

    private string Read(string relativePath) => _host.Read(relativePath);

    private void WriteFile(string path, string text, Encoding encoding)
    {
        _host.Memory.CreateDirectory(Path.GetDirectoryName(path)!);
        _host.Memory.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
    }

    private void WriteFile(string path, string text)
    {
        _host.Memory.CreateDirectory(Path.GetDirectoryName(path)!);
        _host.Memory.WriteAllText(path, text);
    }

    private void WriteHostConfig(string yaml)
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, ".kyber-weave"));
        WriteFile(Path.Combine(_host.Root, ".kyber-weave", "kyber-weave.yml"), yaml);
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("6-Docs")]
    [InlineData("doc")]
    [InlineData("documentation")]
    public void DetectsAnExistingConventionalDocsRoot(string existing)
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, existing));

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root);

        Assert.Equal(existing, result.DocsRoot);
        Assert.Equal(DocsRootSource.Convention, result.DocsRootSource);
    }

    [Fact]
    public void PrefersDocsWhenSeveralConventionalRootsExist()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "6-Docs"));
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "docs"));

        Assert.Equal("docs", _host.Scaffolder.Scaffold(_host.Root).DocsRoot);
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
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, ".kyber-weave"));
        WriteFile(
            Path.Combine(_host.Root, ".kyber-weave", "kyber-weave.yml"),
            """
            ontology:
              docs-root: 6-Docs
            """);

        // A docs/ directory would otherwise be detected first.
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "docs"));
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "6-Docs"));

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root);

        Assert.Equal("6-Docs", result.DocsRoot);
        Assert.Equal(DocsRootSource.Configuration, result.DocsRootSource);
        Assert.True(_host.Memory.FileExists(Path.Combine(_host.Root, "6-Docs", "catalog.md")));
        Assert.True(_host.Memory.FileExists(Path.Combine(_host.Root, "6-Docs", "documentation-ontology.md")));
        Assert.False(_host.Memory.FileExists(Path.Combine(_host.Root, "docs", "catalog.md")));
    }

    /// <summary>
    /// The root <c>docs init</c> resolves must match the root <c>docs validate</c> resolves,
    /// even when a config omits <c>docs-root</c> (validate falls back to the product
    /// default). A re-detected conventional root would diverge.
    /// </summary>
    [Fact]
    public void InitResolvesToTheSameRootValidateUsesWhenConfigOmitsDocsRoot()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, ".kyber-weave"));
        WriteFile(
            Path.Combine(_host.Root, ".kyber-weave", "kyber-weave.yml"),
            """
            ontology:
              excluded-files: []
            """);

        // A docs/ directory would be detected by convention, but validate ignores it.
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "docs"));

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root);
        string validateRoot = _host.LoadConfig().Ontology.DocsRoot;

        Assert.Equal(validateRoot, result.DocsRoot);
        Assert.Equal(DocsRootSource.Configuration, result.DocsRootSource);
    }

    /// <summary>
    /// A broken host config is operator state requiring repair. Falling back to convention
    /// would write a corpus that validation cannot load, and an explicit root must not
    /// bypass the same preflight.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("handbook")]
    public void InvalidHostConfigStopsBeforeAnyScaffoldWrite(string? docsRoot)
    {
        const string yaml = "ontology: [unclosed";
        WriteHostConfig(yaml);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => _host.Scaffolder.Scaffold(_host.Root, docsRoot));

        Assert.Contains(KyberWeaveConfigDiagnosticCodes.LoadError, exception.Message, StringComparison.Ordinal);
        Assert.Contains("kyber-weave.yml", exception.Message, StringComparison.Ordinal);
        Assert.Equal(yaml, Read(ConfigPath));
        Assert.Single(_host.AllFiles(_host.Root));
    }

    [Fact]
    public void FallsBackToDocsWhenNoConventionalRootExists()
    {
        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root);

        Assert.Equal("docs", result.DocsRoot);
        Assert.Equal(DocsRootSource.Convention, result.DocsRootSource);
        Assert.True(_host.Memory.DirectoryExists(Path.Combine(_host.Root, "docs")));
    }

    [Fact]
    public void AnExplicitDocsRootOverridesDetection()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "docs"));

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, docsRoot: "handbook");

        Assert.Equal("handbook", result.DocsRoot);
        Assert.Equal(DocsRootSource.Explicit, result.DocsRootSource);
        Assert.True(_host.Memory.FileExists(Path.Combine(_host.Root, "handbook", "catalog.md")));
    }

    [Fact]
    public void TheEmittedConfigPointsAtTheResolvedDocsRoot()
    {
        _host.Scaffolder.Scaffold(_host.Root, docsRoot: "handbook");

        KyberWeaveConfig config = _host.LoadConfig();

        Assert.Equal("handbook", config.Ontology.DocsRoot);
    }

    /// <summary>
    /// The product defaults carry five DevOps exclusions from the repository the ontology
    /// was first built for. A fresh host must not inherit them.
    /// </summary>
    [Fact]
    public void TheEmittedConfigClearsInheritedFileExclusions()
    {
        _host.Scaffolder.Scaffold(_host.Root);

        KyberWeaveConfig config = _host.LoadConfig();

        Assert.Empty(config.Ontology.ExcludedFiles);
    }

    [Fact]
    public void ExistingFilesAreLeftAloneAndReportedAsSuch()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "docs"));
        WriteFile(Path.Combine(_host.Root, "docs", "catalog.md"), "hand written");

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root);

        Assert.Equal("hand written", Read("docs/catalog.md"));
        ScaffoldedFile catalog = result.Files.Single(f => f.RelativePath == "docs/catalog.md");
        Assert.Equal(ScaffoldOutcome.Skipped, catalog.Outcome);
        Assert.Null(catalog.Note);
        Assert.False(catalog.Written);
        Assert.True(result.Files.Single(f => f.RelativePath == "docs/documentation-ontology.md").Written);
    }

    [Fact]
    public void ForceOverwritesExistingFiles()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "docs"));
        WriteFile(Path.Combine(_host.Root, "docs", "catalog.md"), "hand written");

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, force: true);

        Assert.NotEqual("hand written", Read("docs/catalog.md"));
        Assert.True(result.Files.Single(f => f.RelativePath == "docs/catalog.md").Written);
    }

    /// <summary>
    /// Analysis persistence is allowed only after the repository-owned state directory has
    /// the exact narrow ignore entry, and the wiring receipts directory is ignored beside it.
    /// A fresh init must establish that safety without also creating a glossary that has no
    /// reviewed senses.
    /// </summary>
    [Fact]
    public void FreshInitCreatesOnlyTheNarrowLocalStateIgnoresAndNoEmptyGlossary()
    {
        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root);

        Assert.Equal("cache/\nreceipts/\n", Read(".kyber-weave/.gitignore"));
        Assert.True(_host.CacheSafety.IsSafe(_host.Root));
        Assert.False(_host.Memory.FileExists(Path.Combine(_host.Root, "docs", "glossary.md")));
        ScaffoldedFile entry = result.Files.Single(file => file.RelativePath == ".kyber-weave/.gitignore");
        Assert.Equal(ScaffoldOutcome.Created, entry.Outcome);
    }

    /// <summary>
    /// The state ignore file may contain operator-owned entries. Init owns only the narrow
    /// cache line and the receipts line beside it, including under <c>--force</c>, and must
    /// not regenerate the rest.
    /// </summary>
    [Fact]
    public void ForceMergesCacheIgnoreWithoutReplacingExistingLines()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, ".kyber-weave"));
        const string existing = "# operator entry\nlocal-notes/";
        WriteFile(Path.Combine(_host.Root, ".kyber-weave", ".gitignore"), existing);

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, force: true);

        // The operator's bytes are an exact prefix; the boundary newline and the two narrow
        // entries are the only thing appended.
        Assert.Equal(existing + "\ncache/\nreceipts/\n", Read(".kyber-weave/.gitignore"));
        Assert.True(_host.CacheSafety.IsSafe(_host.Root));
        ScaffoldedFile entry = result.Files.Single(file => file.RelativePath == ".kyber-weave/.gitignore");
        Assert.Equal(ScaffoldOutcome.Updated, entry.Outcome);
    }

    /// <summary>
    /// A prior exact entry is not sufficient when a later negation exposes the cache again.
    /// Init must append the narrow rule after that negation so ordinary analysis can persist
    /// safely, while preserving the operator's original lines for inspection.
    /// </summary>
    [Fact]
    public void InitRepairsAnIneffectiveCacheIgnoreWithoutDiscardingItsLines()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, ".kyber-weave"));
        const string existing = "cache/\n!cache/docs-analysis.sqlite3\n";
        WriteFile(Path.Combine(_host.Root, ".kyber-weave", ".gitignore"), existing);
        Assert.False(_host.CacheSafety.IsSafe(_host.Root));

        _host.Scaffolder.Scaffold(_host.Root);

        Assert.Equal(existing + "cache/\nreceipts/\n", Read(".kyber-weave/.gitignore"));
        Assert.True(_host.CacheSafety.IsSafe(_host.Root));
    }

    /// <summary>
    /// Re-running init must not duplicate or reformat an entry the file already carries. The
    /// first run adds the receipts entry the host does not have yet and the second adds
    /// nothing, so the merge stays byte-stable for hosts that maintain other local-state rules.
    /// </summary>
    [Fact]
    public void CacheIgnoreMergeIsIdempotentAndPreservesExistingBytes()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, ".kyber-weave"));
        const string existing = "# local state\ncache/\nlocal-notes/\n";
        WriteFile(Path.Combine(_host.Root, ".kyber-weave", ".gitignore"), existing);

        ScaffoldResult first = _host.Scaffolder.Scaffold(_host.Root);
        ScaffoldResult second = _host.Scaffolder.Scaffold(_host.Root, force: true);

        // The operator's bytes are an exact prefix and the receipts entry joins the cache
        // entry without the cache line being regenerated or repeated.
        Assert.Equal(existing + "receipts/\n", Read(".kyber-weave/.gitignore"));
        Assert.Single(
            _host.Memory.ReadLines(Path.Combine(_host.Root, ".kyber-weave", ".gitignore")),
            line => StringComparer.Ordinal.Equals(line, "cache/"));
        Assert.Single(
            _host.Memory.ReadLines(Path.Combine(_host.Root, ".kyber-weave", ".gitignore")),
            line => StringComparer.Ordinal.Equals(line, "receipts/"));

        // Only the run that had something to append reports a change; the repeat preserves.
        Assert.Equal(
            ScaffoldOutcome.Updated,
            first.Files.Single(file => file.RelativePath == ".kyber-weave/.gitignore").Outcome);
        Assert.Equal(
            ScaffoldOutcome.Preserved,
            second.Files.Single(file => file.RelativePath == ".kyber-weave/.gitignore").Outcome);
    }

    /// <summary>
    /// Reading and rewriting decoded text silently changes an operator-owned file's byte
    /// representation. The merge must append in the detected encoding so the original BOM,
    /// Unicode text, and CRLF bytes remain an exact prefix of the result.
    /// </summary>
    [Fact]
    public void CacheIgnoreMergePreservesExistingEncodingPreambleAndBytePrefix()
    {
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, ".kyber-weave"));
        string path = Path.Combine(_host.Root, ".kyber-weave", ".gitignore");
        const string existing = "# opérateur\r\nlocal-notes/";
        WriteFile(path, existing, Encoding.Unicode);
        byte[] originalBytes = _host.Memory.ReadAllBytes(path);

        _host.Scaffolder.Scaffold(_host.Root);

        byte[] appendedBytes = Encoding.Unicode.GetBytes("\r\ncache/\r\nreceipts/\r\n");
        Assert.Equal(originalBytes.Concat(appendedBytes), _host.Memory.ReadAllBytes(path));
        Assert.True(_host.CacheSafety.IsSafe(_host.Root));
    }

    /// <summary>
    /// A host config carries settings this scaffolder's template knows nothing about —
    /// harness profiles, catalog column overrides, closed vocabularies. Regenerating it
    /// under <c>--force</c> would discard all of them to restate a docs root the file
    /// already had. The one thing the run adds is the MCP declaration the file lacks.
    /// </summary>
    [Fact]
    public void ForceDoesNotOverwriteTheHostConfig()
    {
        WriteHostConfig(HandMaintainedConfig);

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, force: true);

        // The operator's file is an exact prefix of what was written; the declaration is the
        // only thing appended after it.
        Assert.Equal(HandMaintainedConfig + McpWiringFixture.AppendedDeclaration(), Read(ConfigPath));

        ScaffoldedFile entry = result.Files.Single(f => f.RelativePath == ConfigPath);
        Assert.Equal(ScaffoldOutcome.Updated, entry.Outcome);
        Assert.Equal("the mcp declaration appended; the rest of the file is untouched", entry.Note);
        Assert.True(entry.Written);
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

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, docsRoot: "handbook", force: true);

        KyberWeaveConfig config = _host.LoadConfig();
        Assert.Equal("handbook", config.Ontology.DocsRoot);

        // Every other override the operator wrote is still in force.
        Assert.Equal(3, config.Ontology.CatalogComponentColumn);
        Assert.Equal(8, config.Ontology.CatalogOwnerColumn);
        Assert.Equal(["archive"], config.Ontology.ExcludedPathSegments);
        Assert.Equal(".agents/agents", config.Harness.Profiles[HarnessKind.Claude].DirectoryName);

        // Verbatim, not merely semantically: comments and key order are the operator's, and
        // the rewritten docs-root is the only byte of theirs that changed.
        string text = Read(ConfigPath);
        Assert.Equal(
            HandMaintainedConfig.Replace(
                "docs-root: 6-Docs  # moved in 2024", "docs-root: 'handbook'  # moved in 2024",
                StringComparison.Ordinal) + McpWiringFixture.AppendedDeclaration(),
            text);

        Assert.Equal(ScaffoldOutcome.Updated, result.Files.Single(f => f.RelativePath == ConfigPath).Outcome);
    }

    /// <summary>
    /// A re-run that resolves the root out of the config must not rewrite the file it just
    /// read: an unchanged docs root is not a reason to touch operator state. The first run
    /// adds the declaration the file lacks, and the run after it finds both and writes
    /// nothing.
    /// </summary>
    [Fact]
    public void AnUnchangedDocsRootAppendsOnlyTheMcpDeclaration()
    {
        WriteHostConfig(HandMaintainedConfig);

        ScaffoldResult first = _host.Scaffolder.Scaffold(_host.Root);

        string afterFirst = HandMaintainedConfig + McpWiringFixture.AppendedDeclaration();
        Assert.Equal(afterFirst, Read(ConfigPath));
        Assert.Equal(ScaffoldOutcome.Updated, first.Files.Single(f => f.RelativePath == ConfigPath).Outcome);

        ScaffoldResult second = _host.Scaffolder.Scaffold(_host.Root);

        Assert.Equal(afterFirst, Read(ConfigPath));
        Assert.Equal(ScaffoldOutcome.Preserved, second.Files.Single(f => f.RelativePath == ConfigPath).Outcome);
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

        _host.Scaffolder.Scaffold(_host.Root, docsRoot: "handbook");

        KyberWeaveConfig config = _host.LoadConfig();
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

        _host.Scaffolder.Scaffold(_host.Root, docsRoot: "handbook");

        string text = Read(ConfigPath);
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

        _host.Scaffolder.Scaffold(_host.Root, docsRoot: "handbook");

        string text = Read(ConfigPath);
        Assert.Contains("\n  docs-root: 'handbook'\n", text, StringComparison.Ordinal);
        Assert.Equal("handbook", _host.LoadConfig().Ontology.DocsRoot);
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

        _host.Scaffolder.Scaffold(_host.Root, docsRoot: "handbook");

        KyberWeaveConfig config = _host.LoadConfig();
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
        WriteFile(Path.Combine(_host.Root, "kyber-weave.yml"), HandMaintainedConfig);

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, docsRoot: "handbook", force: true);

        Assert.False(_host.Memory.FileExists(Path.Combine(_host.Root, ".kyber-weave", "kyber-weave.yml")));
        Assert.Equal("cache/\nreceipts/\n", Read(".kyber-weave/.gitignore"));

        KyberWeaveConfig config = _host.LoadConfig();
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
        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root);

        Assert.True(_host.Memory.FileExists(
            Path.Combine(_host.Root, result.DocsRoot, "documentation-ontology.md")));
    }

    [Fact]
    public void TheSeededCatalogSuppliesTheOwnerTheScaffoldedDocumentsClaim()
    {
        _host.Scaffolder.Scaffold(_host.Root, owner: "platform-team");

        DocumentSet set = _host.Loader("docs").Load();

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
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => _host.Scaffolder.Scaffold(_host.Root, escaping));

        Assert.Contains("outside the repository root", error.Message, StringComparison.Ordinal);

        // Nothing at all, not merely no documents: the host config resolves inside the
        // root, so a check that only fired per write would leave it behind pointing at a
        // docs root that was rejected a moment later.
        Assert.Empty(_host.AllFiles(_host.Root));
    }

    [Fact]
    public void RefusesAnAbsoluteDocsRoot()
    {
        string elsewhere = Path.Combine(_host.Memory.Root, "elsewhere");
        _host.Memory.CreateDirectory(elsewhere);

        Assert.Throws<ArgumentException>(
            () => _host.Scaffolder.Scaffold(_host.Root, Path.Combine(elsewhere, "pwned")));

        Assert.Empty(_host.AllFiles(elsewhere));
    }

    /// <summary>
    /// Windows resolves paths case-insensitively while Unix does not. Exercise both modes
    /// directly so the regression remains covered on the project's Unix CI runners. A root that is
    /// itself a filesystem root keeps its own separator as the boundary, so the check still means
    /// "inside this repository" rather than "starts with nothing".
    /// </summary>
    [Fact]
    public void ContainmentComparisonUsesTargetPlatformCasingRules()
    {
        const string boundary = @"C:\work\repo\";
        const string differentlyCasedChild = @"c:\WORK\REPO\docs\catalog.md";

        Assert.Equal(boundary, RepositoryBoundary.For(@"C:\work\repo", '\\'));
        Assert.True(RepositoryBoundary.IsWithin(
            differentlyCasedChild, boundary, isWindows: true));
        Assert.False(RepositoryBoundary.IsWithin(
            differentlyCasedChild, boundary, isWindows: false));

        Assert.Equal(@"C:\", RepositoryBoundary.For(@"C:\", '\\'));
        Assert.True(RepositoryBoundary.IsWithin(@"C:\docs\catalog.md", @"C:\", isWindows: true));
        Assert.False(RepositoryBoundary.IsWithin(@"D:\docs\catalog.md", @"C:\", isWindows: true));

        Assert.Equal("/", RepositoryBoundary.For("/", '/'));
        Assert.True(RepositoryBoundary.IsWithin("/docs/catalog.md", "/", isWindows: false));
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
            () => _host.Scaffolder.Scaffold(_host.Root, owner: owner));

        Assert.Empty(_host.AllFiles(_host.Root));
    }

    [Fact]
    public void AcceptsOrdinaryOwnerAndDocsRootValues()
    {
        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, "team-docs", "platform-team");

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

        _host.Scaffolder.Scaffold(_host.Root, docsRoot, owner);

        OntologyConfig ontology = _host.LoadConfig().Ontology;
        DocumentSet set = _host.Loader(ontology).Load();
        Assert.Equal(docsRoot, ontology.DocsRoot);
        Assert.Contains(owner, set.Owners);
        Assert.All(set.Documents, document => Assert.Equal(owner, document.Frontmatter.Owner));
    }

    /// <summary>
    /// Rich standards share the same quoting as stub documents. Without it, an accepted
    /// owner that contains YAML punctuation fails validation only on the --kyber-standards path.
    /// </summary>
    [Fact]
    public void QuotesYamlSpecialCharactersInKyberStandardsFrontmatter()
    {
        const string owner = "platform's: \"core\" #1";

        _host.Scaffolder.Scaffold(_host.Root, owner: owner, kyberStandards: true);

        OntologyConfig ontology = _host.LoadConfig().Ontology;
        DocumentSet set = _host.Loader(ontology).Load();
        DiagnosticReport report = _host.Validator(ontology).Validate(set);
        Assert.Contains(owner, set.Owners);
        Assert.All(set.Documents, document => Assert.Equal(owner, document.Frontmatter.Owner));
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
    }

    /// <summary>Owner whitespace is canonicalized once so YAML and catalog agree.</summary>
    [Fact]
    public void TrimsOwnerBeforeWritingFrontmatterAndCatalog()
    {
        _host.Scaffolder.Scaffold(_host.Root, owner: " platform ");

        OntologyConfig ontology = _host.LoadConfig().Ontology;
        DocumentSet set = _host.Loader(ontology).Load();
        DiagnosticReport report = _host.Validator(ontology).Validate(set);
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

        Assert.Throws<ArgumentException>(() => _host.Scaffolder.Scaffold(_host.Root));

        Assert.Equal(yaml, Read(ConfigPath));
        Assert.Single(_host.AllFiles(_host.Root));
    }

    /// <summary>
    /// A Windows drive colon is data, not YAML structure. The quoted form is what
    /// <c>docs init</c> would write; the loader then refuses it as absolute — covered
    /// separately — so this assertion stops at the quote.
    /// </summary>
    [Fact]
    public void QuotesAWindowsStyleDocsRoot()
    {
        const string docsRoot = "C:/work/repo/docs";
        string yaml = HostConfigYaml.WithDocsRoot("ontology:\n", docsRoot);

        Assert.Contains("docs-root: 'C:/work/repo/docs'", yaml, StringComparison.Ordinal);
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
        string yaml = $"ontology:\n  docs-root: {token}\n";

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

    /// <summary>
    /// A host configuration written with lone CR line endings is read line by line, so the
    /// existing <c>ontology:</c> block is found and only its <c>docs-root</c> changes. Read as one
    /// long line, the block is never found and a second <c>ontology:</c> is appended beneath the
    /// first — a document YAML refuses to load, which leaves the host unable to run any command
    /// until someone repairs the file by hand.
    /// </summary>
    [Fact]
    public void EditsALoneCrHostConfigInPlaceInsteadOfAppendingASecondOntologyBlock()
    {
        WriteHostConfig(
            "# Ours, not the tool's.\r" +
            "ontology:\r" +
            "  docs-root: handbook\r" +
            "  technologies:\r" +
            "    - go\r");

        _host.Scaffolder.Scaffold(_host.Root, "docs", reviewGates: false);

        // The load is the assertion that matters: it is what raised a duplicate-key error before
        // the writer read the file under its own line endings.
        Assert.Equal("docs", _host.LoadConfig().Ontology.DocsRoot);

        string updated = Read(ConfigPath);
        Assert.Single(updated.Split('\r'), line => string.Equals(line, "ontology:", StringComparison.Ordinal));
        Assert.Contains("# Ours, not the tool's.", updated, StringComparison.Ordinal);
        Assert.Contains("  docs-root: 'docs'", updated, StringComparison.Ordinal);
        Assert.Contains("    - go", updated, StringComparison.Ordinal);

        // No LF anywhere: the operator's line breaks, and the append that follows them, are all
        // the file's own style.
        Assert.DoesNotContain("\n", updated, StringComparison.Ordinal);
    }

    /// <summary>The whole point: a freshly initialized repository validates clean.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("handbook")]
    public void AFreshlyScaffoldedCorpusPassesDocsValidate(string? docsRoot)
    {
        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, docsRoot);

        OntologyConfig ontology = _host.LoadConfig().Ontology;
        DocumentSet set = _host.Loader(ontology).Load();
        DiagnosticReport report = _host.Validator(ontology).Validate(set);

        // The ontology reference, the catalog, the corpus index, and one index per
        // scaffolded folder — every file the command claims to have written is governed.
        Assert.Equal(3 + DocsLayout.Folders.Count, set.Documents.Count);
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
        Assert.Equal(result.DocsRoot, ontology.DocsRoot);
    }

    /// <summary>
    /// Structure is created by code, not left to a skill: a folder that exists only when
    /// someone remembered to make it is a folder half the repositories will not have.
    /// </summary>
    [Fact]
    public void CreatesEveryCanonicalFolderWithItsOwnIndex()
    {
        _host.Scaffolder.Scaffold(_host.Root, "docs");

        foreach (string folder in DocsLayout.Folders)
        {
            Assert.Contains($"id: {folder}/index", Read($"docs/{folder}/README.md"), StringComparison.Ordinal);
            Assert.Contains("doc-type: index", Read($"docs/{folder}/README.md"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublishesTheRegistryInANewAgentsFileWhenTheRepositoryHasNone()
    {
        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, "docs");

        string agents = Read("AGENTS.md");
        Assert.Contains(ConfigRegMarkdown.StartMarker, agents, StringComparison.Ordinal);
        Assert.Contains("- **<docs-root>**: `docs`", agents, StringComparison.Ordinal);
        Assert.Contains("- **<component-catalog>**: `docs/catalog.md`", agents, StringComparison.Ordinal);
        Assert.Contains(result.Files, f => f is { RelativePath: "AGENTS.md", Outcome: ScaffoldOutcome.Created });
    }

    /// <summary>
    /// The block is generated and the rest of the file is not. A run rewrites the one and
    /// returns the other byte for byte — including without <c>--force</c>, because a stale
    /// registry sends agents to paths that moved.
    /// </summary>
    [Fact]
    public void RewritesOnlyTheGeneratedRegionOfAHandAuthoredAgentsFile()
    {
        WriteFile(Path.Combine(_host.Root, "AGENTS.md"),
            $"""
            # Working in this repository

            Hand-authored guidance that must survive.

            {ConfigRegMarkdown.StartMarker}
            - **<docs-root>**: `somewhere-else`
            {ConfigRegMarkdown.EndMarker}

            ## House style

            Also hand-authored.
            """);

        _host.Scaffolder.Scaffold(_host.Root, "docs", force: false);

        string agents = Read("AGENTS.md");
        Assert.Contains("Hand-authored guidance that must survive.", agents, StringComparison.Ordinal);
        Assert.Contains("Also hand-authored.", agents, StringComparison.Ordinal);
        Assert.Contains("- **<docs-root>**: `docs`", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("somewhere-else", agents, StringComparison.Ordinal);
    }

    [Fact]
    public void RendersAutonomyAsAPropertyLineOnlyWhenTheKeyIsSet()
    {
        _host.Scaffolder.Scaffold(_host.Root, "docs");
        Assert.DoesNotContain("<autonomy>", Read("AGENTS.md"), StringComparison.Ordinal);

        WriteHostConfig("ontology:\n  docs-root: docs\nautonomy: autonomous\n");
        _host.Scaffolder.Scaffold(_host.Root);

        Assert.Contains("- **<autonomy>**: `autonomous`\n", Read("AGENTS.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void AutonomyRenderingIsIdempotentAndFollowsTheSetting()
    {
        WriteHostConfig("ontology:\n  docs-root: docs\nautonomy: supervised\n");
        _host.Scaffolder.Scaffold(_host.Root);
        string first = Read("AGENTS.md");

        ScaffoldResult second = _host.Scaffolder.Scaffold(_host.Root);

        Assert.Equal(first, Read("AGENTS.md"));
        Assert.Contains(second.Files, f => f is { RelativePath: "AGENTS.md", Outcome: ScaffoldOutcome.Preserved });

        WriteHostConfig("ontology:\n  docs-root: docs\nautonomy: autonomous\n");
        _host.Scaffolder.Scaffold(_host.Root);
        string agents = Read("AGENTS.md");
        Assert.Contains("`autonomous`", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("`supervised`", agents, StringComparison.Ordinal);

        WriteHostConfig("ontology:\n  docs-root: docs\n");
        _host.Scaffolder.Scaffold(_host.Root);
        Assert.DoesNotContain("<autonomy>", Read("AGENTS.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void RerunningLeavesAnAlreadyCurrentRegistryAlone()
    {
        _host.Scaffolder.Scaffold(_host.Root, "docs");
        string first = Read("AGENTS.md");

        ScaffoldResult second = _host.Scaffolder.Scaffold(_host.Root, "docs");

        Assert.Equal(first, Read("AGENTS.md"));
        Assert.Contains(second.Files, f => f is { RelativePath: "AGENTS.md", Outcome: ScaffoldOutcome.Preserved });
    }

    /// <summary>
    /// One declared list creates the folder, publishes the registry property, and legalizes
    /// the frontmatter value — so a freshly scaffolded standard validates without an edit.
    /// </summary>
    [Fact]
    public void ADeclaredTechnologyIsScaffoldedPublishedAndValid()
    {
        WriteHostConfig("ontology:\n  docs-root: docs\n  technologies:\n    - github-actions\n");

        _host.Scaffolder.Scaffold(_host.Root);

        string standard = Read("docs/standards/github-actions/github-actions.standards.md");
        Assert.Contains("doc-type: coding-standard", standard, StringComparison.Ordinal);
        Assert.Contains("technology: github-actions", standard, StringComparison.Ordinal);
        Assert.Contains(
            "- **<github-actions-coding-standard>**: `docs/standards/github-actions/github-actions.standards.md`",
            Read("AGENTS.md"),
            StringComparison.Ordinal);

        OntologyConfig ontology = _host.LoadConfig().Ontology;
        DiagnosticReport report = _host.Validator(ontology)
            .Validate(_host.Loader(ontology).Load());
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
    }

    /// <summary>
    /// Replacing to the end of the file would delete whatever an operator wrote below an
    /// unclosed marker, and which content that is cannot be known here.
    /// </summary>
    [Fact]
    public void RefusesToGuessAtAnUnclosedMarker()
    {
        WriteFile(Path.Combine(_host.Root, "AGENTS.md"),
            $"# Working here\n\n{ConfigRegMarkdown.StartMarker}\n\n## Mine\n\nKeep me.\n");

        Assert.Throws<InvalidDataException>(() => _host.Scaffolder.Scaffold(_host.Root, "docs"));
        Assert.Contains("Keep me.", Read("AGENTS.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Initializing with kyberStandards enabled on a fresh repository creates all 11 rich
    /// coding standards from embedded templates, registers them in ontology.technologies
    /// and AGENTS.md Config Reg, and passes docs validation cleanly.
    /// </summary>
    [Fact]
    public void ScaffoldWithKyberStandardsOnFreshRepoScaffoldsAllRichStandardsAndUpdatesConfig()
    {
        string today = MemoryScaffoldHost.Today;

        ScaffoldResult result = _host.Scaffolder.Scaffold(_host.Root, kyberStandards: true);

        // All 11 standards under <docs-root>/standards/<tech>/ are created with their four files
        Assert.Equal(KyberStandardsTemplates.All.Count, KyberStandardsTemplates.All.Count(tech =>
            result.Files.Any(f => f.RelativePath == $"{result.DocsRoot}/standards/{tech}/{tech}.standards.md"
                && f is { Outcome: ScaffoldOutcome.Created, Written: true })));

        // Contents match the rendered template for each part
        foreach (string tech in KyberStandardsTemplates.All)
        {
            foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
            {
                string relativePath =
                    $"{result.DocsRoot}/standards/{tech}/{KyberStandardsTemplates.FileName(tech, part)}";
                string content = Read(relativePath);
                string expected = Templates.RenderPart(tech, part, "unassigned", today);
                Assert.Equal(expected, content);
            }
        }

        // .kyber-weave/kyber-weave.yml contains all 11 technologies under ontology.technologies
        KyberWeaveConfig config = _host.LoadConfig();
        Assert.Equal(
            KyberStandardsTemplates.All.OrderBy(t => t),
            config.Ontology.Technologies.OrderBy(t => t));

        string configYaml = Read(ConfigPath);
        Assert.Contains("technologies:", configYaml, StringComparison.Ordinal);
        foreach (string tech in KyberStandardsTemplates.All)
        {
            Assert.Contains($"- {tech}", configYaml, StringComparison.Ordinal);
        }

        // AGENTS.md Config Reg block has all 11 <{tech}-coding-standard> properties
        string agents = Read("AGENTS.md");
        foreach (string tech in KyberStandardsTemplates.All)
        {
            Assert.Contains(
                $"- **<{tech}-coding-standard>**: `{result.DocsRoot}/standards/{tech}/{tech}.standards.md`",
                agents,
                StringComparison.Ordinal);
        }

        // Running DocSpecValidator.Validate yields 0 findings
        DocumentSet set = _host.Loader(config.Ontology).Load();
        DiagnosticReport report = _host.Validator(config.Ontology).Validate(set);
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
        Assert.Equal(3 + DocsLayout.Folders.Count + (KyberStandardsTemplates.All.Count * 3) + 1, set.Documents.Count);
    }

    /// <summary>
    /// Scaffolding with kyberStandards skips pre-existing standard files when force is false,
    /// and overwrites them with the rich template when force is true.
    /// </summary>
    [Fact]
    public void ScaffoldWithKyberStandardsSkipsExistingWithoutForceAndOverwritesWithForce()
    {
        string today = MemoryScaffoldHost.Today;
        _host.Memory.CreateDirectory(Path.Combine(_host.Root, "docs", "standards", "csharp"));
        const string customText =
            """
            ---
            id: standards/csharp
            title: Custom C# coding standard
            doc-type: coding-standard
            status: draft
            technology: csharp
            owner: custom-team
            last-reviewed: 2026-08-01
            ---

            # Custom C# coding standard
            Custom rules that must not be overwritten without --force.
            """;
        WriteFile(Path.Combine(_host.Root, "docs", "standards", "csharp", "csharp.standards.md"), customText);

        // Run without force -> Skipped, file untouched
        ScaffoldResult result1 = _host.Scaffolder.Scaffold(_host.Root, kyberStandards: true, force: false);
        ScaffoldedFile csharpFile1 = result1.Files.Single(f => f.RelativePath == "docs/standards/csharp/csharp.standards.md");
        Assert.Equal(ScaffoldOutcome.Skipped, csharpFile1.Outcome);
        Assert.False(csharpFile1.Written);
        Assert.Equal(customText, Read("docs/standards/csharp/csharp.standards.md"));

        // Run with force -> Updated, file overwritten with rich template
        ScaffoldResult result2 = _host.Scaffolder.Scaffold(_host.Root, kyberStandards: true, force: true);
        ScaffoldedFile csharpFile2 = result2.Files.Single(f => f.RelativePath == "docs/standards/csharp/csharp.standards.md");
        Assert.Equal(ScaffoldOutcome.Updated, csharpFile2.Outcome);
        Assert.True(csharpFile2.Written);
        string overwrittenText = Read("docs/standards/csharp/csharp.standards.md");
        Assert.NotEqual(customText, overwrittenText);
        Assert.Equal(Templates.Render("csharp", "unassigned", today), overwrittenText);
    }
}
