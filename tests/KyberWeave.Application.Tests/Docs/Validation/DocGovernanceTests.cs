using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;

using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Export;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using KyberWeave.Data.CodeGraph;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>
/// A rules or skills document may name a technology or a role only when it sits in that
/// scope's folder. A skills document is the one type that must name one of the two.
/// </summary>
public class SkillAndScopeTests
{
    private static readonly OntologyConfig Scoped =
        new() { Technologies = ["dotnet"], Roles = ["reviewer"] };

    private static string Document(string docType, string? technology, string? role) =>
        $"""
        ---
        id: standards/scope
        title: Scoped document
        doc-type: {docType}
        status: current
        {(technology is null ? string.Empty : $"technology: {technology}")}
        {(role is null ? string.Empty : $"role: {role}")}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # Scoped document
        """;

    private static DocFixture CompleteFolder(DocFixture fixture, string scope, string scopeKey)
    {
        string standard = $"""
            ---
            id: standards/{scope}
            title: {scope} standard
            doc-type: coding-standard
            status: current
            {scopeKey}: {scope}
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---

            # {scope}

            ## Naming and layout

            ## Coding standards

            ## Testing

            ## Commands

            ## Security and data handling

            ## Done checklist
            """;
        string rules = $"""
            ---
            id: standards/{scope}/rules
            title: {scope} rules
            doc-type: rule
            status: current
            {scopeKey}: {scope}
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---

            # {scope} rules
            """;

        return fixture
            .Write($"6-Docs/standards/{scope}/{scope}.standards.md", standard)
            .Write($"6-Docs/standards/{scope}/{scope}.rules.md", rules)
            .Write($"6-Docs/standards/{scope}/{scope}.arbiter-rules.yml", "rules: []\n");
    }

    [Fact]
    public void ASkillInItsTechnologysFolderIsValid()
    {
        using DocFixture fixture = CompleteFolder(DocFixture.CreateInMemory().WithCatalog(), "dotnet", "technology")
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", Document("skill", "dotnet", null));

        Assert.False(fixture.Validate(Scoped).HasErrors);
    }

    [Fact]
    public void ASkillInItsRolesFolderIsValid()
    {
        using DocFixture fixture = CompleteFolder(DocFixture.CreateInMemory().WithCatalog(), "reviewer", "role")
            .Write("6-Docs/standards/reviewer/reviewer.skills.md", Document("skill", null, "reviewer"));

        Assert.False(fixture.Validate(Scoped).HasErrors);
    }

    [Fact]
    public void ASkillNamingNeitherScopeIsSPEC011()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", Document("skill", null, null));

        DiagnosticReport report = fixture.Validate(Scoped);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MissingScope);
    }

    [Fact]
    public void ARuleNamingATechnologyInItsFolderIsValid()
    {
        using DocFixture fixture = CompleteFolder(DocFixture.CreateInMemory().WithCatalog(), "dotnet", "technology")
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", Document("skill", "dotnet", null));

        Assert.False(fixture.Validate(Scoped).HasErrors);
    }

    [Fact]
    public void ARuleNamingATechnologyOutsideItsFolderIsSPEC007()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/rules/backend.md", Document("rule", "dotnet", null));

        DiagnosticReport report = fixture.Validate(Scoped);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MisplacedTechnology);
    }

    [Fact]
    public void ARoleOnAReferenceIsSPEC007()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/reference/roles.md", Document("reference", null, "reviewer"));

        DiagnosticReport report = fixture.Validate(Scoped);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MisplacedTechnology);
    }

    [Fact]
    public void AnUndeclaredRoleIsSPEC002()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/standards/devops/devops.skills.md", Document("skill", null, "devops"));

        DiagnosticReport report = fixture.Validate(Scoped);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidVocabulary
            && i.Message.Contains("devops", StringComparison.Ordinal));
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/standards/dotnet/README.md", Standard("dotnet"));

        Assert.False(fixture.Validate(DotnetDeclared).HasErrors);
    }

    [Fact]
    public void UndeclaredTechnologyIsSPEC002()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/standards/rust/README.md", Standard("rust"));

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.InvalidVocabulary);
    }

    [Fact]
    public void MissingScopeOnAStandardIsSPEC011()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
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

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MissingScope);
    }

    [Fact]
    public void TechnologyOnAnotherDocTypeIsSPEC007()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/standards/dotnet/README.md", Standard("dotnet", docType: "reference"));

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MisplacedTechnology);
    }

    [Fact]
    public void TechnologyDisagreeingWithItsFolderIsSPEC007()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
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
        Assert.False(OntologyConfig.ProductDefaults.IsRequired(DocType.CodingStandard, "technology"));
    }
}

public class DocGovernanceSpecValidatorTests
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/reference/thing.md", ValidReference);

        DiagnosticReport report = fixture.Validate();

        Assert.False(report.HasErrors);
    }

    [Fact]
    public void MissingFrontmatterIsSPEC001()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/reference/thing.md", "# Just a heading\n");

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.MissingFrontmatter);
    }

    [Fact]
    public void UnknownDocTypeIsSPEC002()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/reference/thing.md", """
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/reference/thing.md", """
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/reference/thing.md", """
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().WithSourceRoot("1-Presentation/Api")
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/operations/process.md", """
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().WithSourceRoot("1-Presentation/Api")
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/reference/thing.md", """
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/reference/thing.md", """
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/api/onboarding.md", """
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/reference/one.md", ValidReference)
            .Write("6-Docs/reference/two.md", ValidReference);

        DiagnosticReport report = fixture.Validate();

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.BadReference && i.Message.Contains("is declared by 2 documents", StringComparison.Ordinal));
    }

    [Fact]
    public void UnresolvableDecidedByIsSPEC006()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/reference/thing.md", """
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/archive/old.md", "# No frontmatter here\n");

        Assert.Empty(fixture.LoadSubjects());
    }

    [Fact]
    public void VendoredDevOpsSkillFilesAreOutOfScope()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/DevOps/incremental-build.md", "---\nname: upstream/skill\n---\n");

        Assert.Empty(fixture.LoadSubjects());
    }

    [Fact]
    public void HyphenatedKeysBindToModel()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().WithSourceRoot("1-Presentation/Api")
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
        Assert.Equal(["SomeService"], doc.CodeRefs());
        Assert.Equal(["GET /api/thing"], doc.ApiEndpoints);
    }

    [Fact]
    public void KeywordsDeserializeIntoModel()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
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

        Assert.Equal(["dashboard", "tauri"], doc.Keywords());
        Assert.NotNull(doc.Frontmatter.Keywords);
        Assert.Equal(["dashboard", "tauri"], doc.Frontmatter.Keywords);
    }

    [Fact]
    public void AliasesDeserializeIntoKeywordsWhenKeywordsOmitted()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
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

        Assert.Equal(["dashboard", "tauri"], doc.Keywords());
        Assert.NotNull(doc.Frontmatter.Aliases);
        Assert.Equal(["dashboard", "tauri"], doc.Frontmatter.Aliases);
    }

    [Fact]
    public void ValidKeywordsProduceNoDiagnostics()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
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

        Diagnostic item = Assert.Single(report.Items, i => i.Code == DocSpecValidator.InvalidVocabulary);
        Assert.Equal("Each keyword must be a non-empty string.", item.Hint);
    }

    [Fact]
    public void NullKeywordEntryIsSPEC002()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
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

        Diagnostic item = Assert.Single(report.Items, i => i.Code == DocSpecValidator.InvalidVocabulary);
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
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().Write("6-Docs/reference/thing.md", """
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
        ScriptedProcessExecutor sqlite = new(_ => new ProcessResult(0, string.Empty, string.Empty));
        CodeGraphResolverAdapter resolver = new(
            Path.Combine(fixture.Root, ".codegraph", "codegraph.db"),
            fixture.Memory!,
            fixture.Paths,
            fixture.Environment,
            sqlite,
            new CodeGraphIndexStaleness(fixture.Memory!, new ScriptedProcessRunner()));
        DiagnosticReport report = new DocDriftLinter(resolver).Validate(fixture.Load());

        Assert.False(resolver.IsAvailable);
        Assert.Empty(sqlite.Calls);
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

/// <summary>
/// The closeout gate reads the harvest declaration off the model, so the hyphenated
/// frontmatter keys must bind to it: documentation-impact names the authoring scale and
/// impact-components names the components it touches. Selected by the closeout runner
/// as the DocGovernanceTests suite.
/// </summary>
public sealed class DocGovernanceTests
{
    [Fact]
    public void DocumentationImpactAndImpactComponentsBindToTheModel()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog().WithSourceRoot("1-Presentation/Api")
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
                documentation-impact: extends-component
                impact-components:
                  - MotorcycleRAG API
                ---
                """);

        DocumentModel doc = Assert.Single(fixture.LoadSubjects());

        Assert.Equal("extends-component", doc.Frontmatter.DocumentationImpact);
        Assert.NotNull(doc.Frontmatter.ImpactComponents);
        Assert.Equal(["MotorcycleRAG API"], doc.Frontmatter.ImpactComponents);
    }
}
