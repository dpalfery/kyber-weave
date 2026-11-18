using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests;
using KyberWeave.Tests.Configuration;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Application.Tests.Configuration;

/// <summary>
/// T1 — ontology config model + loader contract. Product defaults must mirror today's
/// hardcoded vocabularies and required-key matrix; host overrides must be mergeable.
/// </summary>
public class OntologyConfigTests
{
    private readonly InMemoryConfigFileReader _config = new();

    private static readonly string[] ExpectedDocTypes =
    [
        "architecture", "onboarding", "requirements", "adr", "plan", "spec", "todo",
        "runbook", "reference", "rule", "governance", "index", "coding-standard", "skill"
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
        string yamlPath = _config.WriteYaml("""
            ontology:
              required-keys:
                reference:
                  - audience
                onboarding: []
            """);

        OntologyConfig merged = _config.LoadOntologyMerged(OntologyConfig.ProductDefaults, yamlPath);

        Assert.True(merged.IsRequired(DocType.Reference, "audience"));
        Assert.False(merged.IsRequired(DocType.Onboarding, "component"));
        Assert.False(merged.IsRequired(DocType.Onboarding, "source-root"));
    }

    [Fact]
    public void OverrideYamlCanDeclareRolesBesideTechnologies()
    {
        string yamlPath = _config.WriteYaml("""
            ontology:
              technologies:
                - csharp
              roles:
                - conductor
                - reviewer
            """);

        OntologyConfig merged = _config.LoadOntologyMerged(OntologyConfig.ProductDefaults, yamlPath);

        Assert.Equal(["csharp"], merged.Technologies);
        Assert.Equal(["conductor", "reviewer"], merged.Roles);
    }

    [Fact]
    public void AnUndeclaredRoleYamlEntryIsRejected()
    {
        string yamlPath = _config.WriteYaml("""
            ontology:
              roles:
                - Reviewer Role
            """);

        Assert.Throws<YamlException>(() =>
            _config.LoadOntologyMerged(OntologyConfig.ProductDefaults, yamlPath));
    }

    [Fact]
    public void OverrideYamlCanChangeExclusionsDocsRootAndCatalogColumns()
    {
        string yamlPath = _config.WriteYaml("""
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

        OntologyConfig merged = _config.LoadOntologyMerged(OntologyConfig.ProductDefaults, yamlPath);

        Assert.Equal("custom-docs", merged.DocsRoot);
        Assert.Equal(["archive", "vendor-cache"], merged.ExcludedPathSegments);
        Assert.Contains("imports/upstream.md", merged.ExcludedFiles);
        Assert.Equal(2, merged.CatalogComponentColumn);
        Assert.Equal(7, merged.CatalogOwnerColumn);
    }

    [Fact]
    public void OverrideYamlWiresDocumentLoaderAndDocSpecValidator()
    {
        string yamlPath = _config.WriteYaml("""
            ontology:
              docs-root: docs
              excluded-segments:
                - archive
              excluded-files:
                - vendored/skill.md
            """);

        OntologyConfig config = _config.LoadOntologyMerged(OntologyConfig.ProductDefaults, yamlPath);
        OntologyConfigDocFixture fixture = new OntologyConfigDocFixture(config);

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
        string yamlPath = _config.WriteYaml("ontology: [unclosed");

        OntologyConfigLoadResult result = _config.TryLoadOntology(yamlPath);

        Assert.False(result.Success);
        Assert.NotNull(result.ParseError);
        Assert.Null(result.Config);
    }

    [Fact]
    public void UnknownRequiredKeysDocTypeIsRejected()
    {
        string yamlPath = _config.WriteYaml("""
            ontology:
              required-keys:
                not-a-real-type:
                  - audience
            """);

        OntologyConfigLoadResult result = _config.TryLoadOntology(yamlPath);

        Assert.False(result.Success);
        Assert.Contains("not-a-real-type", result.ParseError, StringComparison.Ordinal);
        Assert.Null(result.Config);

        YamlException ex = Assert.ThrowsAny<YamlException>(
            () => _config.LoadOntologyMerged(OntologyConfig.ProductDefaults, yamlPath));
        Assert.Contains("not-a-real-type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullTechnologyEntryIsRejected()
    {
        string yamlPath = _config.WriteYaml("""
            ontology:
              technologies:
                - csharp
                - null
            """);

        YamlException thrown = Assert.ThrowsAny<YamlException>(
            () => _config.LoadOntologyMerged(OntologyConfig.ProductDefaults, yamlPath));
        Assert.Contains("null entry", thrown.Message, StringComparison.Ordinal);

        OntologyConfigLoadResult result = _config.TryLoadOntology(yamlPath);
        Assert.False(result.Success);
        Assert.Contains("null entry", result.ParseError, StringComparison.Ordinal);
        Assert.Null(result.Config);
    }

    [Fact]
    public void ANullRequiredKeysMappingKeyFailsTheLoadRatherThanThrowing()
    {
        string yamlPath = _config.WriteYaml("""
            ontology:
              required-keys:
                ~:
                  - audience
            """);

        OntologyConfigLoadResult result = _config.TryLoadOntology(yamlPath);

        Assert.False(result.Success);
        Assert.NotNull(result.ParseError);
        Assert.Null(result.Config);
    }

    [Fact]
    public void AnEmptyRequiredKeysDocTypeIsRejected()
    {
        string yamlPath = _config.WriteYaml("""
            ontology:
              required-keys:
                "":
                  - audience
            """);

        OntologyConfigLoadResult result = _config.TryLoadOntology(yamlPath);

        Assert.False(result.Success);
        Assert.Contains("required-keys", result.ParseError, StringComparison.Ordinal);
        Assert.Null(result.Config);
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

    private sealed class OntologyConfigDocFixture
    {
        private readonly OntologyConfig _config;
        private readonly DocFixture _docs = DocFixture.CreateInMemory();

        public OntologyConfigDocFixture(OntologyConfig config)
        {
            _config = config;
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
            _ = _docs.Write(relativePath, content);
            return this;
        }

        public IReadOnlyList<DocumentModel> LoadSubjects() =>
            _docs.Loader(_config).Load().Documents
                .Where(d => d.RelativePath != $"{_config.DocsRoot}/catalog.md")
                .ToList();

        public DiagnosticReport Validate() => _docs.Validate(_config);
    }
}
