using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A technology template names at most one parent it builds on — React on TypeScript, Tauri
/// on Rust. A task in the child receives the parent's rules and skills as well. The parent
/// must be declared, must not be the child, and must not itself have a parent.
/// </summary>
public class StandardsBuildsOnTests
{
    private static readonly OntologyConfig Declared = new()
    {
        Technologies = ["react", "typescript", "tauri", "rust", "csharp", "data-access-layer"]
    };

    private static string Standard(string technology, string? buildsOn) =>
        $"""
        ---
        id: standards/{technology}
        title: {technology} standard
        doc-type: coding-standard
        status: current
        technology: {technology}
        {(buildsOn is null ? string.Empty : $"builds-on: {buildsOn}")}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # {technology}

        ## Naming and layout

        ## Coding standards

        ## Testing

        ## Commands

        ## Security and data handling

        ## Done checklist
        """;

    private static string Scoped(string technology, string kind) =>
        $"""
        ---
        id: standards/{technology}/{kind}
        title: {technology} {kind}
        doc-type: {(kind == "rules" ? "rule" : "skill")}
        status: current
        technology: {technology}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # {technology} {kind}
        """;

    private static DocFixture Folder(DocFixture fixture, string technology, string? buildsOn = null) =>
        fixture
            .Write($"6-Docs/standards/{technology}/{technology}.standards.md", Standard(technology, buildsOn))
            .Write($"6-Docs/standards/{technology}/{technology}.rules.md", Scoped(technology, "rules"))
            .Write($"6-Docs/standards/{technology}/{technology}.skills.md", Scoped(technology, "skills"))
            .Write($"6-Docs/standards/{technology}/{technology}.arbiter-rules.yml", "rules: []\n");

    [Fact]
    public void ADeclaredParentIsAccepted()
    {
        using DocFixture fixture = Folder(
            Folder(new DocFixture().WithCatalog(), "typescript"), "react", "typescript");

        Assert.False(fixture.Validate(Declared).HasErrors);
    }

    [Fact]
    public void AnUndeclaredParentIsAnErrorNamingTheFolder()
    {
        using DocFixture fixture = Folder(new DocFixture().WithCatalog(), "react", "javascript");

        DiagnosticReport report = fixture.Validate(Declared);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidBuildsOn
            && i.Message.Contains("react", StringComparison.Ordinal)
            && i.Message.Contains("javascript", StringComparison.Ordinal));
    }

    [Fact]
    public void ASelfReferenceIsAnError()
    {
        using DocFixture fixture = Folder(new DocFixture().WithCatalog(), "react", "react");

        DiagnosticReport report = fixture.Validate(Declared);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidBuildsOn
            && i.Message.Contains("react", StringComparison.Ordinal));
    }

    [Fact]
    public void AParentThatItselfNamesAParentIsAnError()
    {
        using DocFixture fixture = Folder(
            Folder(new DocFixture().WithCatalog(), "typescript", "csharp"),
            "react",
            "typescript");

        DiagnosticReport report = fixture.Validate(Declared);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidBuildsOn
            && i.Message.Contains("react", StringComparison.Ordinal)
            && i.Message.Contains("typescript", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two standards naming one technology are two claims about its parent. The second one must
    /// not hide the first: each is checked, so an undeclared parent is reported wherever it sits.
    /// </summary>
    [Fact]
    public void TwoStandardsThatNameOneTechnologyAreEachChecked()
    {
        using DocFixture fixture = Folder(new DocFixture().WithCatalog(), "react", "typescript")
            .Write(
                "6-Docs/standards/react/react-extra.standards.md",
                Standard("react", "javascript").Replace(
                    "id: standards/react", "id: standards/react/extra", StringComparison.Ordinal));

        DiagnosticReport report = fixture.Validate(Declared);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidBuildsOn
            && i.Message.Contains("javascript", StringComparison.Ordinal));
    }

    /// <summary>
    /// A role-scoped standard names no technology of its own, but it still may name a parent,
    /// and that parent has to be declared. Skipping the check for it would accept any value.
    /// </summary>
    [Fact]
    public void ARoleScopedStandardWithAnUndeclaredParentIsAnError()
    {
        const string RoleStandard = """
            ---
            id: standards/reviewer
            title: reviewer standard
            doc-type: coding-standard
            status: current
            role: reviewer
            builds-on: nosuchtech
            owner: Maintainers
            last-reviewed: 2026-07-21
            ---

            # reviewer

            ## Naming and layout

            ## Coding standards

            ## Testing

            ## Commands

            ## Security and data handling

            ## Done checklist
            """;
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/reviewer/reviewer.standards.md", RoleStandard);
        OntologyConfig ontology = new() { Technologies = ["csharp"], Roles = ["reviewer"] };

        DiagnosticReport report = fixture.Validate(ontology);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.InvalidBuildsOn
            && i.Message.Contains("reviewer", StringComparison.Ordinal)
            && i.Message.Contains("nosuchtech", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildsOnOutsideAStandardIsAnError()
    {
        using DocFixture fixture = Folder(new DocFixture().WithCatalog(), "react")
            .Write("6-Docs/rules/frontend.md", """
                ---
                id: rules/frontend
                title: Frontend rules
                doc-type: rule
                status: current
                builds-on: typescript
                owner: Maintainers
                last-reviewed: 2026-07-21
                ---

                # Frontend rules
                """);

        DiagnosticReport report = fixture.Validate(Declared);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.InvalidBuildsOn);
    }
}
