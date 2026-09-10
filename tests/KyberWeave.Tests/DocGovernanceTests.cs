using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Export;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A throwaway repository tree on disk. The loader and validator both read real files,
/// so the fixture builds a real directory rather than mocking the file system.
/// </summary>
internal sealed class DocFixture : IDisposable
{
    public string Root { get; }

    public DocFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "sf-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "6-Docs"));
    }

    /// <summary>
    /// Writes a catalog with the vocabularies the validator checks against. The catalog
    /// is itself an in-scope document, so it carries conforming frontmatter — exactly as
    /// the real one must.
    /// </summary>
    public DocFixture WithCatalog()
    {
        Write("6-Docs/catalog.md", """
            ---
            id: system/catalog
            title: Component Catalog
            doc-type: index
            status: current
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---

            # Catalog

            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            | --- | --- | --- | --- | --- | --- | --- | --- |
            | MotorcycleRAG API | Application | `1-Presentation/Api` | [README](x) | [docs](y) | API maintainers | 2026-07-21 | Current |
            | MotorcycleRAG system | System | repository root | [README](x) | [docs](y) | Maintainers | 2026-07-21 | Current |
            """);
        return this;
    }

    /// <summary>Documents other than the fixture catalog itself.</summary>
    public IReadOnlyList<DocumentModel> LoadSubjects() =>
        Load().Documents.Where(d => d.RelativePath != "6-Docs/catalog.md").ToList();

    public DocFixture WithSourceRoot(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return this;
    }

    public DocFixture Write(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public DocumentSet Load() => new DocumentLoader(Root).Load();

    public DiagnosticReport Validate() => new DocSpecValidator(Root).Validate(Load());

    /// <summary>Validates against a host ontology — the technologies a repository declared.</summary>
    public DiagnosticReport Validate(OntologyConfig ontology) =>
        new DocSpecValidator(Root, ontology).Validate(new DocumentLoader(Root, ontology).Load());

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

/// <summary>
/// A standard's technology must agree with the vocabulary, the doc-type and the folder,
/// because the configuration registry publishes one property per declared technology and
/// points it at the folder of that name. Any disagreement produces a standard that
/// nothing resolves.
/// </summary>
public class TechnologyDeclarationTests
{
    private static readonly OntologyConfig DotnetDeclared =
        new OntologyConfig { Technologies = ["dotnet"] };

    private static string Standard(string technology, string docType = "coding-standard") =>
        $"""
        ---
        id: standards/{technology}
        title: {technology} coding standard
        doc-type: {docType}
        status: current
        technology: {technology}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # {technology}
        """;

    [Fact]
    public void DeclaredTechnologyInItsOwnFolderHasNoFindings()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/README.md", Standard("dotnet"));

        Assert.False(fixture.Validate(DotnetDeclared).HasErrors);
    }

    [Fact]
    public void UndeclaredTechnologyIsSPEC002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/rust/README.md", Standard("rust"));

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.InvalidVocabulary);
    }

    [Fact]
    public void MissingTechnologyOnAStandardIsSPEC003()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/README.md", """
                ---
                id: standards/dotnet
                title: dotnet coding standard
                doc-type: coding-standard
                status: current
                owner: Maintainers
                last-reviewed: 2026-07-21
                ---
                """);

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MissingRequiredKey);
    }

    [Fact]
    public void TechnologyOnAnotherDocTypeIsSPEC007()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/README.md", Standard("dotnet", docType: "reference"));

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MisplacedTechnology);
    }

    [Fact]
    public void TechnologyDisagreeingWithItsFolderIsSPEC007()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/backend/README.md", Standard("dotnet"));

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MisplacedTechnology);
    }

    /// <summary>
    /// A standard covers code in every component, so requiring one would be a false claim
    /// about its reach — and asserting that here keeps a later "make it consistent with
    /// the other types" edit from quietly introducing one.
    /// </summary>
    [Fact]
    public void AStandardNeedsNoComponent()
    {
        Assert.False(OntologyConfig.ProductDefaults.IsRequired(DocType.CodingStandard, "component"));
        Assert.True(OntologyConfig.ProductDefaults.IsRequired(DocType.CodingStandard, "technology"));
    }
}

public class DocSpecValidatorTests
{
    private const string ValidReference = """
        ---
        id: reference/thing
        title: A Thing
        doc-type: reference
        status: current
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # A Thing
        """;

    [Fact]
    public void ConformingDocumentProducesNoFindings()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", ValidReference);

        DiagnosticReport report = fixture.Validate();

        Assert.False(report.HasErrors);
    }

    [Fact]
    public void MissingFrontmatterIsSPEC001()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/thing.md", "# Just a heading\n");

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MissingFrontmatter);
    }

    [Fact]
    public void UnknownDocTypeIsSPEC002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: rumination
            status: current
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---
            """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidVocabulary && i.Message.Contains("rumination", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownStatusIsSPEC002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: reference
            status: probably-fine
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---
            """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidVocabulary && i.Message.Contains("probably-fine", StringComparison.Ordinal));
    }

    [Fact]
    public void NonIsoLastReviewedIsSPEC002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: reference
            status: current
            owner: Maintainers
            last-reviewed: July 2026
            ---
            """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidVocabulary && i.Message.Contains("last-reviewed", StringComparison.Ordinal));
    }

    [Fact]
    public void ArchitectureWithoutCodeRefsIsSPEC003()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().WithSourceRoot("1-Presentation/Api")
            .Write("6-Docs/api/architecture.md", """
                ---
                id: api/architecture
                title: API Architecture
                doc-type: architecture
                status: current
                component: MotorcycleRAG API
                source-root: 1-Presentation/Api
                owner: API maintainers
                last-reviewed: 2026-07-21
                code-refs: []
                ---
                """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.MissingRequiredKey && i.Message.Contains("code-refs", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessOnlyRunbookWithoutSourceRootNeedsNoCodeRefs()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/operations/process.md", """
            ---
            id: operations/process
            title: A Process Runbook
            doc-type: runbook
            status: current
            component: MotorcycleRAG system
            owner: Maintainers
            last-reviewed: 2026-07-21
            code-refs: []
            ---
            """);

        DiagnosticReport report = fixture.Validate();

        Assert.DoesNotContain(report.Items, i => i.Code == DocSpecValidator.MissingRequiredKey);
    }

    [Fact]
    public void RunbookWithSourceRootRequiresCodeRefs()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().WithSourceRoot("1-Presentation/Api")
            .Write("6-Docs/operations/component.md", """
                ---
                id: operations/component
                title: A Component Runbook
                doc-type: runbook
                status: current
                component: MotorcycleRAG API
                source-root: 1-Presentation/Api
                owner: API maintainers
                last-reviewed: 2026-07-21
                code-refs: []
                ---
                """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.MissingRequiredKey && i.Message.Contains("code-refs", StringComparison.Ordinal));
    }

    [Fact]
    public void UncatalogedComponentIsSPEC004()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: requirements
            status: current
            component: MotorcycleRAG Imaginary
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---
            """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.UnknownCatalogValue && i.Message.Contains("component", StringComparison.Ordinal));
    }

    [Fact]
    public void UncatalogedOwnerIsSPEC004()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: reference
            status: current
            owner: Some Other Team
            last-reviewed: 2026-07-21
            ---
            """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.UnknownCatalogValue && i.Message.Contains("owner", StringComparison.Ordinal));
    }

    [Fact]
    public void NonexistentSourceRootIsSPEC005()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/api/onboarding.md", """
            ---
            id: api/onboarding
            title: API Onboarding
            doc-type: onboarding
            status: current
            component: MotorcycleRAG API
            source-root: 1-Presentation/DoesNotExist
            owner: API maintainers
            last-reviewed: 2026-07-21
            ---
            """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.SourceRootMissing);
    }

    [Fact]
    public void DuplicateIdIsSPEC006()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/one.md", ValidReference)
            .Write("6-Docs/reference/two.md", ValidReference);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.BadReference && i.Message.Contains("is declared by 2 documents", StringComparison.Ordinal));
    }

    [Fact]
    public void UnresolvableDecidedByIsSPEC006()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: reference
            status: current
            owner: Maintainers
            last-reviewed: 2026-07-21
            decided-by:
              - adr/not-a-real-decision
            ---
            """);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.BadReference && i.Message.Contains("decided-by", StringComparison.Ordinal));
    }

    [Fact]
    public void ArchivedDocumentsAreOutOfScope()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/archive/old.md", "# No frontmatter here\n");

        Assert.Empty(fixture.LoadSubjects());
    }

    [Fact]
    public void VendoredDevOpsSkillFilesAreOutOfScope()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/DevOps/incremental-build.md", "---\nname: upstream/skill\n---\n");

        Assert.Empty(fixture.LoadSubjects());
    }

    [Fact]
    public void HyphenatedKeysBindToModel()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().WithSourceRoot("1-Presentation/Api")
            .Write("6-Docs/api/architecture.md", """
                ---
                id: api/architecture
                title: API Architecture
                doc-type: architecture
                status: current
                component: MotorcycleRAG API
                source-root: 1-Presentation/Api
                owner: API maintainers
                last-reviewed: 2026-07-21
                code-refs:
                  - SomeService
                api-endpoints:
                  - GET /api/thing
                ---
                """);

        DocumentModel doc = Assert.Single(fixture.LoadSubjects());

        Assert.Equal(DocType.Architecture, doc.DocType);
        Assert.Equal(DocStatus.Current, doc.Status);
        Assert.Equal("1-Presentation/Api", doc.Frontmatter.SourceRoot);
        Assert.Equal(["SomeService"], doc.CodeRefs);
        Assert.Equal(["GET /api/thing"], doc.ApiEndpoints);
    }

    [Fact]
    public void KeywordsDeserializeIntoModel()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/thing.md", """
                ---
                id: reference/thing
                title: A Thing
                doc-type: reference
                status: current
                owner: Maintainers
                last-reviewed: 2026-07-21
                keywords:
                  - dashboard
                  - tauri
                ---

                # A Thing
                """);

        DocumentModel doc = Assert.Single(fixture.LoadSubjects());

        Assert.Equal(["dashboard", "tauri"], doc.Keywords);
        Assert.NotNull(doc.Frontmatter.Keywords);
        Assert.Equal(["dashboard", "tauri"], doc.Frontmatter.Keywords);
    }

    [Fact]
    public void AliasesDeserializeIntoKeywordsWhenKeywordsOmitted()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/thing.md", """
                ---
                id: reference/thing
                title: A Thing
                doc-type: reference
                status: current
                owner: Maintainers
                last-reviewed: 2026-07-21
                aliases:
                  - dashboard
                  - tauri
                ---

                # A Thing
                """);

        DocumentModel doc = Assert.Single(fixture.LoadSubjects());

        Assert.Equal(["dashboard", "tauri"], doc.Keywords);
        Assert.NotNull(doc.Frontmatter.Aliases);
        Assert.Equal(["dashboard", "tauri"], doc.Frontmatter.Aliases);
    }

    [Fact]
    public void ValidKeywordsProduceNoDiagnostics()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/thing.md", """
                ---
                id: reference/thing
                title: A Thing
                doc-type: reference
                status: current
                owner: Maintainers
                last-reviewed: 2026-07-21
                keywords:
                  - dashboard
                  - tauri
                ---

                # A Thing
                """);

        DiagnosticReport report = fixture.Validate();

        Assert.False(report.HasErrors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceKeywordIsSPEC002(string keyword)
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/thing.md", $"""
                ---
                id: reference/thing
                title: A Thing
                doc-type: reference
                status: current
                owner: Maintainers
                last-reviewed: 2026-07-21
                keywords:
                  - "{keyword}"
                ---

                # A Thing
                """);

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(report.Items.Where(i => i.Code == DocSpecValidator.InvalidVocabulary));
        Assert.Equal("Each keyword must be a non-empty string.", item.Hint);
    }

    [Fact]
    public void NullKeywordEntryIsSPEC002()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/thing.md", """
                ---
                id: reference/thing
                title: A Thing
                doc-type: reference
                status: current
                owner: Maintainers
                last-reviewed: 2026-07-21
                keywords:
                  -
                ---

                # A Thing
                """);

        DiagnosticReport report = fixture.Validate();

        Diagnostic item = Assert.Single(report.Items.Where(i => i.Code == DocSpecValidator.InvalidVocabulary));
        Assert.Equal("Each keyword must be a non-empty string.", item.Hint);
    }
}

public class DocLinkAndExportTests
{
    [Theory]
    [InlineData("6-Docs/a/b.md", "c.md", "6-Docs/a/c.md")]
    [InlineData("6-Docs/a/b.md", "../x/y.md", "6-Docs/x/y.md")]
    [InlineData("6-Docs/a/b.md", "./same.md", "6-Docs/a/same.md")]
    [InlineData("6-Docs/a/b.md", "../../root.md", "root.md")]
    public void ResolveLinkNormalizesRelativeTargets(string from, string link, string expected)
    {
        Assert.Equal(expected, DocGraphExporter.ResolveLink(from, link));
    }

    [Fact]
    public void ResolveLinkRejectsEscapingTheRepository()
    {
        Assert.Null(DocGraphExporter.ResolveLink("a.md", "../../../etc/passwd"));
    }

    [Fact]
    public void ExtractRelativeLinksSkipsAbsoluteAndExternal()
    {
        IReadOnlyList<string> links = DocumentLoader.ExtractRelativeLinks(
            "[a](x.md) [b](https://example.com) [c](mailto:a@b.c) [d](/abs.md) [e](y.md#frag)");

        Assert.Equal(["x.md", "y.md"], links);
    }
}

public class DocDriftLinterTests
{
    [Fact]
    public void MissingIndexIsReportedAsCriticalNotSilentlyPassed()
    {
        using DocFixture fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: reference
            status: current
            owner: Maintainers
            last-reviewed: 2026-07-21
            code-refs:
              - AnySymbol
            ---
            """);

        // No .codegraph/ in the fixture tree: an unverifiable drift check must fail loudly
        // rather than report a clean run.
        CodeGraphResolverAdapter resolver = CodeGraphResolverAdapter.ForRepository(fixture.Root);
        DiagnosticReport report = new DocDriftLinter(resolver).Validate(fixture.Load());

        Assert.False(resolver.IsAvailable);
        Assert.True(report.HasCritical);
    }
}

public class DiagnosticSubjectTests
{
    [Fact]
    public void SubjectIsTheOnlyNameForWhatAFindingIsAbout()
    {
        Diagnostic diagnostic = new Diagnostic("KW-DOC-SPEC-001", Severity.Error, "message", "subject-value");

        Assert.Equal("subject-value", diagnostic.Subject);
        Assert.Null(typeof(Diagnostic).GetProperty("SkillName"));
    }
}
