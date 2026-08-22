using System.Text;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Docs.Model;
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

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);

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

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);

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

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);
        string validateRoot = KyberWeaveConfigLoader.Load(_temp.Path).Ontology.DocsRoot;

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
            () => DocsScaffolder.Scaffold(_temp.Path, docsRoot));

        Assert.Contains(KyberWeaveConfigLoader.ConfigLoadErrorCode, exception.Message, StringComparison.Ordinal);
        Assert.Contains("kyber-weave.yml", exception.Message, StringComparison.Ordinal);
        Assert.Equal(yaml, Read(ConfigPath));
        Assert.Single(Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// The CLI must translate the preflight failure into its normal handled-error contract;
    /// otherwise Spectre reports an unhandled error and exits 255.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("handbook")]
    public void CliReturnsOneAndTheConfigDiagnosticWithoutWriting(string? docsRoot)
    {
        const string yaml = "ontology: [unclosed";
        WriteHostConfig(yaml);

        (int ExitCode, ScaffoldResult? Result, string? Error) attempt = DocsInitCommand.TryScaffold(new DocsInitSettings
        {
            Path = _temp.Path,
            DocsRoot = docsRoot,
            NoSkill = true
        });

        Assert.Equal(1, attempt.ExitCode);
        Assert.Null(attempt.Result);
        Assert.Contains(KyberWeaveConfigLoader.ConfigLoadErrorCode, attempt.Error, StringComparison.Ordinal);
        Assert.Contains("kyber-weave.yml", attempt.Error, StringComparison.Ordinal);
        Assert.Equal(yaml, Read(ConfigPath));
        Assert.Single(Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FallsBackToDocsWhenNoConventionalRootExists()
    {
        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("docs", result.DocsRoot);
        Assert.Equal(DocsRootSource.Convention, result.DocsRootSource);
        Assert.True(Directory.Exists(Path.Combine(_temp.Path, "docs")));
    }

    [Fact]
    public void AnExplicitDocsRootOverridesDetection()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        Assert.Equal("handbook", result.DocsRoot);
        Assert.Equal(DocsRootSource.Explicit, result.DocsRootSource);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "handbook", "catalog.md")));
    }

    [Fact]
    public void TheEmittedConfigPointsAtTheResolvedDocsRoot()
    {
        DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_temp.Path);

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

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_temp.Path);

        Assert.Empty(config.Ontology.ExcludedFiles);
    }

    [Fact]
    public void ExistingFilesAreLeftAloneAndReportedAsSuch()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));
        File.WriteAllText(Path.Combine(_temp.Path, "docs", "catalog.md"), "hand written");

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);

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
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs"));
        File.WriteAllText(Path.Combine(_temp.Path, "docs", "catalog.md"), "hand written");

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, force: true);

        Assert.NotEqual("hand written", Read("docs/catalog.md"));
        Assert.True(result.Files.Single(f => f.RelativePath == "docs/catalog.md").Written);
    }

    /// <summary>
    /// Analysis persistence is allowed only after the repository-owned state directory has
    /// the exact narrow ignore entry. A fresh init must establish that safety without also
    /// creating a glossary that has no reviewed senses.
    /// </summary>
    [Fact]
    public void FreshInitCreatesOnlyTheNarrowCacheIgnoreAndNoEmptyGlossary()
    {
        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal("cache/\n", Read(".kyber-weave/.gitignore"));
        Assert.True(AnalysisCacheSafety.IsSafe(_temp.Path));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "docs", "glossary.md")));
        ScaffoldedFile entry = result.Files.Single(file => file.RelativePath == ".kyber-weave/.gitignore");
        Assert.Equal(ScaffoldOutcome.Created, entry.Outcome);
    }

    /// <summary>
    /// The state ignore file may contain operator-owned entries. Init owns only the narrow
    /// cache line, including under <c>--force</c>, and must not regenerate the rest.
    /// </summary>
    [Fact]
    public void ForceMergesCacheIgnoreWithoutReplacingExistingLines()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_temp.Path, ".kyber-weave", ".gitignore"),
            "# operator entry\nlocal-notes/");

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, force: true);

        Assert.Equal(
            "# operator entry\nlocal-notes/\ncache/\n",
            Read(".kyber-weave/.gitignore"));
        Assert.True(AnalysisCacheSafety.IsSafe(_temp.Path));
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
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".kyber-weave"));
        const string existing = "cache/\n!cache/docs-analysis.sqlite3\n";
        File.WriteAllText(Path.Combine(_temp.Path, ".kyber-weave", ".gitignore"), existing);
        Assert.False(AnalysisCacheSafety.IsSafe(_temp.Path));

        DocsScaffolder.Scaffold(_temp.Path);

        Assert.Equal(existing + "cache/\n", Read(".kyber-weave/.gitignore"));
        Assert.True(AnalysisCacheSafety.IsSafe(_temp.Path));
    }

    /// <summary>
    /// Re-running init must not duplicate or reformat an already effective ignore entry.
    /// This keeps the merge byte-stable for hosts that maintain other local-state rules.
    /// </summary>
    [Fact]
    public void CacheIgnoreMergeIsIdempotentAndPreservesExistingBytes()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".kyber-weave"));
        const string existing = "# local state\ncache/\nlocal-notes/\n";
        File.WriteAllText(Path.Combine(_temp.Path, ".kyber-weave", ".gitignore"), existing);

        ScaffoldResult first = DocsScaffolder.Scaffold(_temp.Path);
        ScaffoldResult second = DocsScaffolder.Scaffold(_temp.Path, force: true);

        Assert.Equal(existing, Read(".kyber-weave/.gitignore"));
        Assert.Single(
            File.ReadAllLines(Path.Combine(_temp.Path, ".kyber-weave", ".gitignore")),
            line => StringComparer.Ordinal.Equals(line, "cache/"));
        Assert.All(
            new[] { first, second },
            result => Assert.Equal(
                ScaffoldOutcome.Preserved,
                result.Files.Single(file => file.RelativePath == ".kyber-weave/.gitignore").Outcome));
    }

    /// <summary>
    /// Reading and rewriting decoded text silently changes an operator-owned file's byte
    /// representation. The merge must append in the detected encoding so the original BOM,
    /// Unicode text, and CRLF bytes remain an exact prefix of the result.
    /// </summary>
    [Fact]
    public void CacheIgnoreMergePreservesExistingEncodingPreambleAndBytePrefix()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, ".kyber-weave"));
        string path = Path.Combine(_temp.Path, ".kyber-weave", ".gitignore");
        const string existing = "# opérateur\r\nlocal-notes/";
        File.WriteAllText(path, existing, Encoding.Unicode);
        byte[] originalBytes = File.ReadAllBytes(path);

        DocsScaffolder.Scaffold(_temp.Path);

        byte[] appendedBytes = Encoding.Unicode.GetBytes("\r\ncache/\r\n");
        Assert.Equal(originalBytes.Concat(appendedBytes), File.ReadAllBytes(path));
        Assert.True(AnalysisCacheSafety.IsSafe(_temp.Path));
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

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, force: true);

        Assert.Equal(HandMaintainedConfig, Read(ConfigPath));

        ScaffoldedFile entry = result.Files.Single(f => f.RelativePath == ConfigPath);
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

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook", force: true);

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_temp.Path);
        Assert.Equal("handbook", config.Ontology.DocsRoot);

        // Every other override the operator wrote is still in force.
        Assert.Equal(3, config.Ontology.CatalogComponentColumn);
        Assert.Equal(8, config.Ontology.CatalogOwnerColumn);
        Assert.Equal(["archive"], config.Ontology.ExcludedPathSegments);
        Assert.Equal(".agents/agents", config.Harness.Profiles[HarnessKind.Claude].DirectoryName);

        // Verbatim, not merely semantically: comments and key order are the operator's.
        string text = Read(ConfigPath);
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

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);

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

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_temp.Path);
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

        DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook");

        string text = Read(ConfigPath);
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

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_temp.Path);
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

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, docsRoot: "handbook", force: true);

        Assert.False(File.Exists(Path.Combine(_temp.Path, ".kyber-weave", "kyber-weave.yml")));
        Assert.Equal("cache/\n", Read(".kyber-weave/.gitignore"));

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_temp.Path);
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
        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path);

        Assert.True(File.Exists(
            Path.Combine(_temp.Path, result.DocsRoot, "documentation-ontology.md")));
    }

    [Fact]
    public void TheSeededCatalogSuppliesTheOwnerTheScaffoldedDocumentsClaim()
    {
        DocsScaffolder.Scaffold(_temp.Path, owner: "platform-team");

        DocumentSet set = new DocumentLoader(_temp.Path, "docs").Load();

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
        using TempDirectory elsewhere = new TempDirectory();

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
        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, "team-docs", "platform-team");

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

        OntologyConfig ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        DocumentSet set = new DocumentLoader(_temp.Path, ontology).Load();
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

        DocsScaffolder.Scaffold(_temp.Path, owner: owner, kyberStandards: true);

        OntologyConfig ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        DocumentSet set = new DocumentLoader(_temp.Path, ontology).Load();
        DiagnosticReport report = new DocSpecValidator(_temp.Path, ontology).Validate(set);
        Assert.Contains(owner, set.Owners);
        Assert.All(set.Documents, document => Assert.Equal(owner, document.Frontmatter.Owner));
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
    }

    /// <summary>Owner whitespace is canonicalized once so YAML and catalog agree.</summary>
    [Fact]
    public void TrimsOwnerBeforeWritingFrontmatterAndCatalog()
    {
        DocsScaffolder.Scaffold(_temp.Path, owner: " platform ");

        OntologyConfig ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        DocumentSet set = new DocumentLoader(_temp.Path, ontology).Load();
        DiagnosticReport report = new DocSpecValidator(_temp.Path, ontology).Validate(set);
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

    /// <summary>The whole point: a freshly initialized repository validates clean.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("handbook")]
    public void AFreshlyScaffoldedCorpusPassesDocsValidate(string? docsRoot)
    {
        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, docsRoot);

        OntologyConfig ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        DocumentSet set = new DocumentLoader(_temp.Path, ontology).Load();
        DiagnosticReport report = new DocSpecValidator(_temp.Path, ontology).Validate(set);

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
        DocsScaffolder.Scaffold(_temp.Path, "docs");

        foreach (string folder in DocsLayout.Folders)
        {
            Assert.Contains($"id: {folder}/index", Read($"docs/{folder}/README.md"), StringComparison.Ordinal);
            Assert.Contains("doc-type: index", Read($"docs/{folder}/README.md"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublishesTheRegistryInANewAgentsFileWhenTheRepositoryHasNone()
    {
        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, "docs");

        string agents = Read("AGENTS.md");
        Assert.Contains(ConfigRegMarkdown.StartMarker, agents, StringComparison.Ordinal);
        Assert.Contains("- **<docs-root>**: `docs`", agents, StringComparison.Ordinal);
        Assert.Contains("- **<component-catalog>**: `docs/catalog.md`", agents, StringComparison.Ordinal);
        Assert.Contains(result.Files, f => f.RelativePath == "AGENTS.md" && f.Outcome == ScaffoldOutcome.Created);
    }

    /// <summary>
    /// The block is generated and the rest of the file is not. A run rewrites the one and
    /// returns the other byte for byte — including without <c>--force</c>, because a stale
    /// registry sends agents to paths that moved.
    /// </summary>
    [Fact]
    public void RewritesOnlyTheGeneratedRegionOfAHandAuthoredAgentsFile()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "AGENTS.md"),
            $"""
            # Working in this repository

            Hand-authored guidance that must survive.

            {ConfigRegMarkdown.StartMarker}
            - **<docs-root>**: `somewhere-else`
            {ConfigRegMarkdown.EndMarker}

            ## House style

            Also hand-authored.
            """);

        DocsScaffolder.Scaffold(_temp.Path, "docs", force: false);

        string agents = Read("AGENTS.md");
        Assert.Contains("Hand-authored guidance that must survive.", agents, StringComparison.Ordinal);
        Assert.Contains("Also hand-authored.", agents, StringComparison.Ordinal);
        Assert.Contains("- **<docs-root>**: `docs`", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("somewhere-else", agents, StringComparison.Ordinal);
    }

    [Fact]
    public void RerunningLeavesAnAlreadyCurrentRegistryAlone()
    {
        DocsScaffolder.Scaffold(_temp.Path, "docs");
        string first = Read("AGENTS.md");

        ScaffoldResult second = DocsScaffolder.Scaffold(_temp.Path, "docs");

        Assert.Equal(first, Read("AGENTS.md"));
        Assert.Contains(second.Files, f => f.RelativePath == "AGENTS.md" && f.Outcome == ScaffoldOutcome.Preserved);
    }

    /// <summary>
    /// One declared list creates the folder, publishes the registry property, and legalizes
    /// the frontmatter value — so a freshly scaffolded standard validates without an edit.
    /// </summary>
    [Fact]
    public void ADeclaredTechnologyIsScaffoldedPublishedAndValid()
    {
        WriteHostConfig("ontology:\n  docs-root: docs\n  technologies:\n    - github-actions\n");

        DocsScaffolder.Scaffold(_temp.Path);

        string standard = Read("docs/standards/github-actions/README.md");
        Assert.Contains("doc-type: coding-standard", standard, StringComparison.Ordinal);
        Assert.Contains("technology: github-actions", standard, StringComparison.Ordinal);
        Assert.Contains(
            "- **<github-actions-coding-standard>**: `docs/standards/github-actions/README.md`",
            Read("AGENTS.md"),
            StringComparison.Ordinal);

        OntologyConfig ontology = KyberWeaveConfigLoader.Load(_temp.Path).Ontology;
        DiagnosticReport report = new DocSpecValidator(_temp.Path, ontology)
            .Validate(new DocumentLoader(_temp.Path, ontology).Load());
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
    }

    /// <summary>
    /// Replacing to the end of the file would delete whatever an operator wrote below an
    /// unclosed marker, and which content that is cannot be known here.
    /// </summary>
    [Fact]
    public void RefusesToGuessAtAnUnclosedMarker()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "AGENTS.md"),
            $"# Working here\n\n{ConfigRegMarkdown.StartMarker}\n\n## Mine\n\nKeep me.\n");

        Assert.Throws<InvalidDataException>(() => DocsScaffolder.Scaffold(_temp.Path, "docs"));
        Assert.Contains("Keep me.", Read("AGENTS.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Initializing with kyberStandards enabled on a fresh repository creates all 10 rich
    /// coding standards from embedded templates, registers them in ontology.technologies
    /// and AGENTS.md Config Reg, and passes docs validation cleanly.
    /// </summary>
    [Fact]
    public void ScaffoldWithKyberStandardsOnFreshRepoScaffoldsAllTenRichStandardsAndUpdatesConfig()
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        ScaffoldResult result = DocsScaffolder.Scaffold(_temp.Path, kyberStandards: true);

        // All 10 standards under <docs-root>/standards/<tech>/README.md are created
        Assert.Equal(KyberStandardsTemplates.All.Count, KyberStandardsTemplates.All.Count(tech =>
            result.Files.Any(f => f.RelativePath == $"{result.DocsRoot}/standards/{tech}/README.md"
                && f.Outcome == ScaffoldOutcome.Created
                && f.Written)));

        // Contents match KyberStandardsTemplates.Render(tech, owner, date)
        foreach (string tech in KyberStandardsTemplates.All)
        {
            string relativePath = $"{result.DocsRoot}/standards/{tech}/README.md";
            string content = Read(relativePath);
            string expected = KyberStandardsTemplates.Render(tech, "unassigned", today);
            Assert.Equal(expected, content);
        }

        // .kyber-weave/kyber-weave.yml contains all 10 technologies under ontology.technologies
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(_temp.Path);
        Assert.Equal(
            KyberStandardsTemplates.All.OrderBy(t => t),
            config.Ontology.Technologies.OrderBy(t => t));

        string configYaml = Read(ConfigPath);
        Assert.Contains("technologies:", configYaml, StringComparison.Ordinal);
        foreach (string tech in KyberStandardsTemplates.All)
        {
            Assert.Contains($"- {tech}", configYaml, StringComparison.Ordinal);
        }

        // AGENTS.md Config Reg block has all 10 <{tech}-coding-standard> properties
        string agents = Read("AGENTS.md");
        foreach (string tech in KyberStandardsTemplates.All)
        {
            Assert.Contains(
                $"- **<{tech}-coding-standard>**: `{result.DocsRoot}/standards/{tech}/README.md`",
                agents,
                StringComparison.Ordinal);
        }

        // Running DocSpecValidator.Validate yields 0 findings
        DocumentSet set = new DocumentLoader(_temp.Path, config.Ontology).Load();
        DiagnosticReport report = new DocSpecValidator(_temp.Path, config.Ontology).Validate(set);
        Assert.False(report.HasErrors, string.Join("; ", report.Items.Select(i => $"{i.Code} {i.Message}")));
        Assert.Equal(3 + DocsLayout.Folders.Count + KyberStandardsTemplates.All.Count, set.Documents.Count);
    }

    /// <summary>
    /// Scaffolding with kyberStandards skips pre-existing standard files when force is false,
    /// and overwrites them with the rich template when force is true.
    /// </summary>
    [Fact]
    public void ScaffoldWithKyberStandardsSkipsExistingWithoutForceAndOverwritesWithForce()
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(_temp.Path, "docs", "standards", "csharp"));
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
        File.WriteAllText(Path.Combine(_temp.Path, "docs", "standards", "csharp", "README.md"), customText);

        // Run without force -> Skipped, file untouched
        ScaffoldResult result1 = DocsScaffolder.Scaffold(_temp.Path, kyberStandards: true, force: false);
        ScaffoldedFile csharpFile1 = result1.Files.Single(f => f.RelativePath == "docs/standards/csharp/README.md");
        Assert.Equal(ScaffoldOutcome.Skipped, csharpFile1.Outcome);
        Assert.False(csharpFile1.Written);
        Assert.Equal(customText, Read("docs/standards/csharp/README.md"));

        // Run with force -> Updated, file overwritten with rich template
        ScaffoldResult result2 = DocsScaffolder.Scaffold(_temp.Path, kyberStandards: true, force: true);
        ScaffoldedFile csharpFile2 = result2.Files.Single(f => f.RelativePath == "docs/standards/csharp/README.md");
        Assert.Equal(ScaffoldOutcome.Updated, csharpFile2.Outcome);
        Assert.True(csharpFile2.Written);
        string overwrittenText = Read("docs/standards/csharp/README.md");
        Assert.NotEqual(customText, overwrittenText);
        Assert.Equal(KyberStandardsTemplates.Render("csharp", "unassigned", today), overwrittenText);
    }
}
