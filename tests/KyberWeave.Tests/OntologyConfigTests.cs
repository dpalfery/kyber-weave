using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// T1 — ontology config model + loader contract. Product defaults must mirror today's
/// hardcoded vocabularies and required-key matrix; host overrides must be mergeable.
/// </summary>
public class OntologyConfigTests
{
    private static readonly string[] ExpectedDocTypes =
    [
        "architecture", "onboarding", "requirements", "adr", "plan", "spec", "todo",
        "runbook", "reference", "rule", "governance", "index", "coding-standard"
    ];

    private static readonly string[] ExpectedStatuses =
        ["current", "draft", "needs-review", "superseded"];

    [Fact]
    public void ProductDefaultsReproduceClosedVocabularies()
    {
        OntologyConfig config = OntologyConfig.ProductDefaults;

        Assert.Equal(ExpectedDocTypes, config.DocTypes);
        Assert.Equal(ExpectedStatuses, config.Statuses);
        Assert.Equal("6-Docs", config.DocsRoot);
    }

    [Fact]
    public void ProductDefaultsReproduceBaseRequiredKeyMatrix()
    {
        OntologyConfig config = OntologyConfig.ProductDefaults;

        foreach (string key in new[] { "id", "title", "owner", "last-reviewed", "doc-type", "status" })
            Assert.True(config.IsRequiredForAll(key), $"Base key '{key}' must be required for every document.");

        Assert.True(config.IsRequired(DocType.Onboarding, "component"));
        Assert.True(config.IsRequired(DocType.Onboarding, "source-root"));
        Assert.True(config.IsRequired(DocType.Architecture, "component"));
        Assert.True(config.IsRequired(DocType.Requirements, "component"));
        Assert.True(config.IsRequired(DocType.Runbook, "component"));
        Assert.True(config.IsRequired(DocType.Plan, "component"));
        Assert.True(config.IsRequired(DocType.Spec, "component"));
        Assert.True(config.IsRequired(DocType.Todo, "component"));
    }

    [Fact]
    public void ProductDefaultsReproduceExclusionAndCatalogColumnMapping()
    {
        OntologyConfig config = OntologyConfig.ProductDefaults;

        Assert.Equal(["archive", "node_modules", "obj", "bin"], config.ExcludedPathSegments);
        Assert.Contains("DevOps/incremental-build.md", config.ExcludedFiles);
        Assert.Equal(1, config.CatalogComponentColumn);
        Assert.Equal(6, config.CatalogOwnerColumn);
    }

    [Fact]
    public void OverrideYamlCanAddRequiredKeyAndRemovePerTypeRequirement()
    {
        string yamlPath = WriteTempYaml("""
            ontology:
              required-keys:
                reference:
                  - audience
                onboarding: []
            """);

        OntologyConfig merged = OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath);

        Assert.True(merged.IsRequired(DocType.Reference, "audience"));
        Assert.False(merged.IsRequired(DocType.Onboarding, "component"));
        Assert.False(merged.IsRequired(DocType.Onboarding, "source-root"));
    }

    [Fact]
    public void OverrideYamlCanChangeExclusionsDocsRootAndCatalogColumns()
    {
        string yamlPath = WriteTempYaml("""
            ontology:
              docs-root: custom-docs
              excluded-segments:
                - archive
                - vendor-cache
              excluded-files:
                - imports/upstream.md
              catalog:
                component-column: 2
                owner-column: 7
            """);

        OntologyConfig merged = OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath);

        Assert.Equal("custom-docs", merged.DocsRoot);
        Assert.Equal(["archive", "vendor-cache"], merged.ExcludedPathSegments);
        Assert.Contains("imports/upstream.md", merged.ExcludedFiles);
        Assert.Equal(2, merged.CatalogComponentColumn);
        Assert.Equal(7, merged.CatalogOwnerColumn);
    }

    [Fact]
    public void OverrideYamlWiresDocumentLoaderAndDocSpecValidator()
    {
        string yamlPath = WriteTempYaml("""
            ontology:
              docs-root: docs
              excluded-segments:
                - archive
              excluded-files:
                - vendored/skill.md
            """);

        OntologyConfig config = OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath);
        using OntologyConfigDocFixture fixture = new OntologyConfigDocFixture(config);

        fixture.WithCatalog()
            .Write("docs/archive/old.md", "# archived\n")
            .Write("docs/vendored/skill.md", "---\nname: upstream\n---\n")
            .Write("docs/reference/kept.md", ValidReference);

        IReadOnlyList<DocumentModel> subjects = fixture.LoadSubjects();

        Assert.Single(subjects);
        Assert.Equal("docs/reference/kept.md", subjects[0].RelativePath);
        Assert.False(fixture.Validate().HasErrors);
    }

    [Fact]
    public void InvalidYamlReportsParseDiagnosticNotSilentFallback()
    {
        string yamlPath = WriteTempYaml("ontology: [unclosed");

        OntologyConfigLoadResult result = OntologyConfigLoader.TryLoad(yamlPath);

        Assert.False(result.Success);
        Assert.NotNull(result.ParseError);
        Assert.Null(result.Config);
    }

    [Fact]
    public void UnknownRequiredKeysDocTypeIsRejected()
    {
        string yamlPath = WriteTempYaml("""
            ontology:
              required-keys:
                not-a-real-type:
                  - audience
            """);

        OntologyConfigLoadResult result = OntologyConfigLoader.TryLoad(yamlPath);

        Assert.False(result.Success);
        Assert.Contains("not-a-real-type", result.ParseError, StringComparison.Ordinal);
        Assert.Null(result.Config);

        YamlException ex = Assert.ThrowsAny<YamlException>(
            () => OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath));
        Assert.Contains("not-a-real-type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullTechnologyEntryIsRejected()
    {
        using TempDirectory temp = new();
        string yamlPath = Path.Combine(temp.Path, "kyber-weave.yml");
        File.WriteAllText(yamlPath, """
            ontology:
              technologies:
                - csharp
                - null
            """);

        YamlException thrown = Assert.ThrowsAny<YamlException>(
            () => OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath));
        Assert.Contains("null entry", thrown.Message, StringComparison.Ordinal);

        OntologyConfigLoadResult result = OntologyConfigLoader.TryLoad(yamlPath);
        Assert.False(result.Success);
        Assert.Contains("null entry", result.ParseError, StringComparison.Ordinal);
        Assert.Null(result.Config);
    }

    [Fact]
    public void ANullRequiredKeysMappingKeyFailsTheLoadRatherThanThrowing()
    {
        using TempDirectory temp = new();
        string yamlPath = Path.Combine(temp.Path, "kyber-weave.yml");
        File.WriteAllText(yamlPath, """
            ontology:
              required-keys:
                ~:
                  - audience
            """);

        OntologyConfigLoadResult result = OntologyConfigLoader.TryLoad(yamlPath);

        Assert.False(result.Success);
        Assert.NotNull(result.ParseError);
        Assert.Null(result.Config);
    }

    [Fact]
    public void AnEmptyRequiredKeysDocTypeIsRejected()
    {
        using TempDirectory temp = new();
        string yamlPath = Path.Combine(temp.Path, "kyber-weave.yml");
        File.WriteAllText(yamlPath, """
            ontology:
              required-keys:
                "":
                  - audience
            """);

        OntologyConfigLoadResult result = OntologyConfigLoader.TryLoad(yamlPath);

        Assert.False(result.Success);
        Assert.Contains("required-keys", result.ParseError, StringComparison.Ordinal);
        Assert.Null(result.Config);
    }

    [Fact]
    public void CombinedConfigTryLoadSurfacesInvalidYamlAsKWCONFIG001Payload()
    {
        string root = Path.Combine(Path.GetTempPath(), "kw-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DirectoryInfo configDir = Directory.CreateDirectory(Path.Combine(root, ".kyber-weave"));
            File.WriteAllText(Path.Combine(configDir.FullName, "kyber-weave.yml"), "ontology: [unclosed");

            KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(root);

            Assert.False(result.Success);
            Assert.NotNull(result.Error);
            Assert.NotNull(result.ConfigPath);
            Assert.Null(result.Config);
            Assert.Equal(KyberWeaveConfigLoader.ConfigLoadErrorCode, "KW-CONFIG-001");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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

    private static string WriteTempYaml(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "kw-ontology-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class OntologyConfigDocFixture : IDisposable
    {
        private string Root { get; }
        private readonly OntologyConfig _config;

        public OntologyConfigDocFixture(OntologyConfig config)
        {
            _config = config;
            Root = Path.Combine(Path.GetTempPath(), "kw-docs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, config.DocsRoot));
        }

        public OntologyConfigDocFixture WithCatalog()
        {
            Write($"{_config.DocsRoot}/catalog.md", """
                ---
                id: system/catalog
                title: Component Catalog
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-07-21
                ---

                | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
                | --- | --- | --- | --- | --- | --- | --- | --- |
                | Sample API | Application | `src` | [README](x) | [docs](y) | Maintainers | 2026-07-21 | Current |
                """);
            return this;
        }

        public OntologyConfigDocFixture Write(string relativePath, string content)
        {
            string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return this;
        }

        public IReadOnlyList<DocumentModel> LoadSubjects() =>
            new DocumentLoader(Root, _config).Load().Documents
                .Where(d => d.RelativePath != $"{_config.DocsRoot}/catalog.md")
                .ToList();

        public DiagnosticReport Validate() =>
            new DocSpecValidator(Root, _config).Validate(new DocumentLoader(Root, _config).Load());

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
