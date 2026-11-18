using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Tests;

/// <summary>
/// A throwaway repository tree on disk. The loader and validator both read real files,
/// so the fixture builds a real directory rather than mocking the file system.
/// </summary>
internal sealed class DocFixture : IDisposable
{
    /// <summary>Creates the fixture. A factory, not <c>new</c>, because the fluent methods return
    /// this instance and the analyzer cannot follow that ownership across an assembly.</summary>
    public static DocFixture Create() => new();

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

    public DocumentSet Load() => RealDocumentLoader.Create(Root).Load();

    public DiagnosticReport Validate() => new DocSpecValidator(Root).Validate(Load());

    /// <summary>Validates against a host ontology — the technologies a repository declared.</summary>
    public DiagnosticReport Validate(OntologyConfig ontology) =>
        new DocSpecValidator(Root, ontology).Validate(RealDocumentLoader.Create(Root, ontology).Load());

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
