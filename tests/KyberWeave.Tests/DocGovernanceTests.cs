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
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public DocumentSet Load() => new DocumentLoader(Root).Load();

    public DiagnosticReport Validate() => new DocSpecValidator(Root).Validate(Load());

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
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
    public void Conforming_Document_Produces_No_Findings()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", ValidReference);

        var report = fixture.Validate();

        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Missing_Frontmatter_Is_SPEC_001()
    {
        using var fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/thing.md", "# Just a heading\n");

        var report = fixture.Validate();

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MissingFrontmatter);
    }

    [Fact]
    public void Unknown_DocType_Is_SPEC_002()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: rumination
            status: current
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---
            """);

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidVocabulary && i.Message.Contains("rumination", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_Status_Is_SPEC_002()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: reference
            status: probably-fine
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---
            """);

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidVocabulary && i.Message.Contains("probably-fine", StringComparison.Ordinal));
    }

    [Fact]
    public void NonIso_LastReviewed_Is_SPEC_002()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: reference
            status: current
            owner: Maintainers
            last-reviewed: July 2026
            ---
            """);

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidVocabulary && i.Message.Contains("last-reviewed", StringComparison.Ordinal));
    }

    [Fact]
    public void Architecture_Without_CodeRefs_Is_SPEC_003()
    {
        using var fixture = new DocFixture().WithCatalog().WithSourceRoot("1-Presentation/Api")
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

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.MissingRequiredKey && i.Message.Contains("code-refs", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessOnly_Runbook_Without_SourceRoot_Needs_No_CodeRefs()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/operations/process.md", """
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

        var report = fixture.Validate();

        Assert.DoesNotContain(report.Items, i => i.Code == DocSpecValidator.MissingRequiredKey);
    }

    [Fact]
    public void Runbook_With_SourceRoot_Requires_CodeRefs()
    {
        using var fixture = new DocFixture().WithCatalog().WithSourceRoot("1-Presentation/Api")
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

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.MissingRequiredKey && i.Message.Contains("code-refs", StringComparison.Ordinal));
    }

    [Fact]
    public void Uncataloged_Component_Is_SPEC_004()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
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

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.UnknownCatalogValue && i.Message.Contains("component", StringComparison.Ordinal));
    }

    [Fact]
    public void Uncataloged_Owner_Is_SPEC_004()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
            ---
            id: reference/thing
            title: A Thing
            doc-type: reference
            status: current
            owner: Some Other Team
            last-reviewed: 2026-07-21
            ---
            """);

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.UnknownCatalogValue && i.Message.Contains("owner", StringComparison.Ordinal));
    }

    [Fact]
    public void Nonexistent_SourceRoot_Is_SPEC_005()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/api/onboarding.md", """
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

        var report = fixture.Validate();

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.SourceRootMissing);
    }

    [Fact]
    public void Duplicate_Id_Is_SPEC_006()
    {
        using var fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/reference/one.md", ValidReference)
            .Write("6-Docs/reference/two.md", ValidReference);

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.BadReference && i.Message.Contains("is declared by 2 documents", StringComparison.Ordinal));
    }

    [Fact]
    public void Unresolvable_DecidedBy_Is_SPEC_006()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
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

        var report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.BadReference && i.Message.Contains("decided-by", StringComparison.Ordinal));
    }

    [Fact]
    public void Archived_Documents_Are_Out_Of_Scope()
    {
        using var fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/archive/old.md", "# No frontmatter here\n");

        Assert.Empty(fixture.LoadSubjects());
    }

    [Fact]
    public void Vendored_DevOps_Skill_Files_Are_Out_Of_Scope()
    {
        using var fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/DevOps/incremental-build.md", "---\nname: upstream/skill\n---\n");

        Assert.Empty(fixture.LoadSubjects());
    }

    [Fact]
    public void Hyphenated_Keys_Bind_To_Model()
    {
        using var fixture = new DocFixture().WithCatalog().WithSourceRoot("1-Presentation/Api")
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

        var doc = Assert.Single(fixture.LoadSubjects());

        Assert.Equal(DocType.Architecture, doc.DocType);
        Assert.Equal(DocStatus.Current, doc.Status);
        Assert.Equal("1-Presentation/Api", doc.Frontmatter.SourceRoot);
        Assert.Equal(["SomeService"], doc.CodeRefs);
        Assert.Equal(["GET /api/thing"], doc.ApiEndpoints);
    }
}

public class DocLinkAndExportTests
{
    [Theory]
    [InlineData("6-Docs/a/b.md", "c.md", "6-Docs/a/c.md")]
    [InlineData("6-Docs/a/b.md", "../x/y.md", "6-Docs/x/y.md")]
    [InlineData("6-Docs/a/b.md", "./same.md", "6-Docs/a/same.md")]
    [InlineData("6-Docs/a/b.md", "../../root.md", "root.md")]
    public void ResolveLink_Normalizes_Relative_Targets(string from, string link, string expected)
    {
        Assert.Equal(expected, DocGraphExporter.ResolveLink(from, link));
    }

    [Fact]
    public void ResolveLink_Rejects_Escaping_The_Repository()
    {
        Assert.Null(DocGraphExporter.ResolveLink("a.md", "../../../etc/passwd"));
    }

    [Fact]
    public void ExtractRelativeLinks_Skips_Absolute_And_External()
    {
        var links = DocumentLoader.ExtractRelativeLinks(
            "[a](x.md) [b](https://example.com) [c](mailto:a@b.c) [d](/abs.md) [e](y.md#frag)");

        Assert.Equal(["x.md", "y.md"], links);
    }
}

public class DocDriftLinterTests
{
    [Fact]
    public void Missing_Index_Is_Reported_As_Critical_Not_Silently_Passed()
    {
        using var fixture = new DocFixture().WithCatalog().Write("6-Docs/reference/thing.md", """
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
        var resolver = Core.CodeGraph.CodeGraphResolverAdapter.ForRepository(fixture.Root);
        var report = new DocDriftLinter(resolver).Validate(fixture.Load());

        Assert.False(resolver.IsAvailable);
        Assert.True(report.HasCritical);
    }
}

public class DiagnosticSubjectTests
{
    [Fact]
    public void Subject_Is_The_Only_Name_For_What_A_Finding_Is_About()
    {
        var diagnostic = new Diagnostic("KW-DOC-SPEC-001", Severity.Error, "message", "subject-value");

        Assert.Equal("subject-value", diagnostic.Subject);
        Assert.Null(typeof(Diagnostic).GetProperty("SkillName"));
    }
}
