using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T1 — ontology config model + loader contract. Product defaults must mirror today's
/// hardcoded vocabularies and required-key matrix; host overrides must be mergeable.
/// </summary>
public class OntologyConfigTests
{
    private static readonly string[] ExpectedDocTypes =
    [
        "architecture", "onboarding", "requirements", "adr", "plan", "spec",
        "runbook", "reference", "rule", "governance", "index"
    ];

    private static readonly string[] ExpectedStatuses =
        ["current", "draft", "needs-review", "superseded"];

    [Fact]
    public void ProductDefaults_Reproduce_Closed_Vocabularies()
    {
        var config = OntologyConfig.ProductDefaults;

        Assert.Equal(ExpectedDocTypes, config.DocTypes);
        Assert.Equal(ExpectedStatuses, config.Statuses);
        Assert.Equal("6-Docs", config.DocsRoot);
    }

    [Fact]
    public void ProductDefaults_Reproduce_Base_Required_Key_Matrix()
    {
        var config = OntologyConfig.ProductDefaults;

        foreach (var key in new[] { "id", "title", "owner", "last-reviewed", "doc-type", "status" })
            Assert.True(config.IsRequiredForAll(key), $"Base key '{key}' must be required for every document.");

        Assert.True(config.IsRequired(DocType.Onboarding, "component"));
        Assert.True(config.IsRequired(DocType.Onboarding, "source-root"));
        Assert.True(config.IsRequired(DocType.Architecture, "component"));
        Assert.True(config.IsRequired(DocType.Requirements, "component"));
        Assert.True(config.IsRequired(DocType.Runbook, "component"));
        Assert.True(config.IsRequired(DocType.Plan, "component"));
        Assert.True(config.IsRequired(DocType.Spec, "component"));
    }

    [Fact]
    public void ProductDefaults_Reproduce_Exclusion_And_Catalog_Column_Mapping()
    {
        var config = OntologyConfig.ProductDefaults;

        Assert.Equal(["archive", "node_modules", "obj", "bin"], config.ExcludedPathSegments);
        Assert.Contains("DevOps/incremental-build.md", config.ExcludedFiles);
        Assert.Equal(1, config.CatalogComponentColumn);
        Assert.Equal(6, config.CatalogOwnerColumn);
    }

    [Fact]
    public void OverrideYaml_Can_Add_Required_Key_And_Remove_Per_Type_Requirement()
    {
        var yamlPath = WriteTempYaml("""
            ontology:
              required-keys:
                reference:
                  - audience
                onboarding: []
            """);

        var merged = OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath);

        Assert.True(merged.IsRequired(DocType.Reference, "audience"));
        Assert.False(merged.IsRequired(DocType.Onboarding, "component"));
        Assert.False(merged.IsRequired(DocType.Onboarding, "source-root"));
    }

    [Fact]
    public void OverrideYaml_Can_Change_Exclusions_DocsRoot_And_Catalog_Columns()
    {
        var yamlPath = WriteTempYaml("""
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

        var merged = OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath);

        Assert.Equal("custom-docs", merged.DocsRoot);
        Assert.Equal(["archive", "vendor-cache"], merged.ExcludedPathSegments);
        Assert.Contains("imports/upstream.md", merged.ExcludedFiles);
        Assert.Equal(2, merged.CatalogComponentColumn);
        Assert.Equal(7, merged.CatalogOwnerColumn);
    }

    [Fact]
    public void OverrideYaml_Wires_DocumentLoader_And_DocSpecValidator()
    {
        var yamlPath = WriteTempYaml("""
            ontology:
              docs-root: docs
              excluded-segments:
                - archive
              excluded-files:
                - vendored/skill.md
            """);

        var config = OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath);
        using var fixture = new OntologyConfigDocFixture(config);

        fixture.WithCatalog()
            .Write("docs/archive/old.md", "# archived\n")
            .Write("docs/vendored/skill.md", "---\nname: upstream\n---\n")
            .Write("docs/reference/kept.md", ValidReference);

        var subjects = fixture.LoadSubjects();

        Assert.Single(subjects);
        Assert.Equal("docs/reference/kept.md", subjects[0].RelativePath);
        Assert.False(fixture.Validate().HasErrors);
    }

    [Fact]
    public void InvalidYaml_Reports_Parse_Diagnostic_Not_Silent_Fallback()
    {
        var yamlPath = WriteTempYaml("ontology: [unclosed");

        var result = OntologyConfigLoader.TryLoad(yamlPath);

        Assert.False(result.Success);
        Assert.NotNull(result.ParseError);
        Assert.Null(result.Config);
    }

    [Fact]
    public void Unknown_RequiredKeys_DocType_Is_Rejected()
    {
        var yamlPath = WriteTempYaml("""
            ontology:
              required-keys:
                not-a-real-type:
                  - audience
            """);

        var result = OntologyConfigLoader.TryLoad(yamlPath);

        Assert.False(result.Success);
        Assert.Contains("not-a-real-type", result.ParseError, StringComparison.Ordinal);
        Assert.Null(result.Config);

        var ex = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => OntologyConfigLoader.LoadMerged(OntologyConfig.ProductDefaults, yamlPath));
        Assert.Contains("not-a-real-type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedConfig_TryLoad_Surfaces_InvalidYaml_As_KW_CONFIG_001_Payload()
    {
        var root = Path.Combine(Path.GetTempPath(), "kw-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "kyber-weave.yml"), "ontology: [unclosed");

            var result = KyberWeaveConfigLoader.TryLoad(root);

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
        var path = Path.Combine(Path.GetTempPath(), "kw-ontology-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class OntologyConfigDocFixture : IDisposable
    {
        public string Root { get; }
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
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
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
