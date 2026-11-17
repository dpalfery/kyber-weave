using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A technology, role or platform folder is complete when it holds four files named after
/// the folder, and its standard carries the six headings in order. The check is what decides
/// whether a submitted template is complete, so it names the folder and the missing file or
/// heading rather than only saying the folder is wrong.
/// </summary>
public class StandardsFolderShapeTests
{
    private const string SixHeadings = """
        ## Naming and layout

        ## Coding standards

        ## Testing

        ## Commands

        ## Security and data handling

        ## Done checklist
        """;

    private static readonly OntologyConfig DotnetDeclared =
        new() { Technologies = ["dotnet"], Roles = ["reviewer"] };

    private static string Standard(string scope, string headings = SixHeadings) =>
        $"""
        ---
        id: standards/{scope}
        title: {scope} standard
        doc-type: coding-standard
        status: current
        technology: {scope}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # {scope}

        {headings}
        """;

    private static string Rules(string scope) =>
        $"""
        ---
        id: standards/{scope}/rules
        title: {scope} rules
        doc-type: rule
        status: current
        technology: {scope}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # {scope} rules
        """;

    private static string Skills(string scope) =>
        $"""
        ---
        id: standards/{scope}/skills
        title: {scope} skills
        doc-type: skill
        status: current
        technology: {scope}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # {scope} skills
        """;

    private static DocFixture CompleteFolder(string scope) =>
        new DocFixture().WithCatalog()
            .Write($"6-Docs/standards/{scope}/{scope}.standards.md", Standard(scope))
            .Write($"6-Docs/standards/{scope}/{scope}.rules.md", Rules(scope))
            .Write($"6-Docs/standards/{scope}/{scope}.skills.md", Skills(scope))
            .Write($"6-Docs/standards/{scope}/{scope}.arbiter-rules.yml", "rules: []\n");

    [Fact]
    public void ACompleteTechnologyFolderHasNoFindings()
    {
        using DocFixture fixture = CompleteFolder("dotnet");

        Assert.False(fixture.Validate(DotnetDeclared).HasErrors);
    }

    /// <summary>
    /// Every host today holds its standard as README.md. It must keep validating, with one
    /// warning that tells the operator how to reach the new shape.
    /// </summary>
    [Fact]
    public void ALegacyReadmeWarnsOnceAndRaisesNoErrors()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/README.md", Standard("dotnet"));

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.False(report.HasErrors);
        Diagnostic warning = Assert.Single(report.Items);
        Assert.Equal(DocSpecValidator.MissingFolderFile, warning.Code);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("docs standards migrate", warning.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The six-heading shape arrived with the technology standard. A host that renamed its
    /// standard but has not reshaped it is still running, so its own headings are a warning
    /// that names the one command that repairs them, never an error that blocks its runs.
    /// </summary>
    [Fact]
    public void AStandardWithItsOwnHeadingsWarnsAndNamesTheMigrationCommand()
    {
        const string OwnHeadings = """
            ## Stack

            ## Configuration

            ## Commands
            """;
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/dotnet.standards.md", Standard("dotnet", OwnHeadings))
            .Write("6-Docs/standards/dotnet/dotnet.rules.md", Rules("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", Skills("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.arbiter-rules.yml", "rules: []\n");

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.False(report.HasErrors);
        Diagnostic warning = Assert.Single(
            report.Items, i => i.Code == DocSpecValidator.StandardHeadings);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("docs standards migrate", warning.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRulesFileIsAnErrorNamingTheFile()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/dotnet.standards.md", Standard("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", Skills("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.arbiter-rules.yml", "rules: []\n");

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.MissingFolderFile
            && i.Message.Contains("dotnet.rules.md", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyArbiterRulesFileIsValid()
    {
        using DocFixture fixture = CompleteFolder("dotnet");

        Assert.DoesNotContain(
            fixture.Validate(DotnetDeclared).Items,
            i => i.Message.Contains("arbiter-rules", StringComparison.Ordinal));
    }

    [Fact]
    public void AStandardMissingAHeadingWarnsAndNamesTheHeading()
    {
        const string MissingTesting = """
            ## Naming and layout

            ## Coding standards

            ## Commands

            ## Security and data handling

            ## Done checklist
            """;
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/dotnet.standards.md", Standard("dotnet", MissingTesting))
            .Write("6-Docs/standards/dotnet/dotnet.rules.md", Rules("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", Skills("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.arbiter-rules.yml", "rules: []\n");

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.StandardHeadings
            && i.Message.Contains("## Testing", StringComparison.Ordinal));
    }

    [Fact]
    public void AStandardWithHeadingsOutOfOrderWarns()
    {
        const string Reversed = """
            ## Coding standards

            ## Naming and layout

            ## Testing

            ## Commands

            ## Security and data handling

            ## Done checklist
            """;
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/dotnet.standards.md", Standard("dotnet", Reversed))
            .Write("6-Docs/standards/dotnet/dotnet.rules.md", Rules("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", Skills("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.arbiter-rules.yml", "rules: []\n");

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i => i.Code == DocSpecValidator.StandardHeadings);
    }

    /// <summary>
    /// A docs root written as <c>.</c> is the repository itself. The loader records those
    /// documents without a <c>./</c> prefix, so the folder check must build the same paths or its
    /// heading check silently finds no document and reports nothing.
    /// </summary>
    [Fact]
    public void AStandardUnderTheRepositoryRootIsCheckedForItsHeadings()
    {
        const string OwnHeadings = """
            ## Stack

            ## Commands
            """;
        using DocFixture fixture = new DocFixture()
            .Write("standards/dotnet/dotnet.standards.md", Standard("dotnet", OwnHeadings))
            .Write("standards/dotnet/dotnet.rules.md", Rules("dotnet"))
            .Write("standards/dotnet/dotnet.skills.md", Skills("dotnet"))
            .Write("standards/dotnet/dotnet.arbiter-rules.yml", "rules: []\n");
        OntologyConfig ontology = new() { DocsRoots = ["."], Technologies = ["dotnet"] };

        DiagnosticReport report = fixture.Validate(ontology);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.StandardHeadings
            && i.Message.Contains("## Stack", StringComparison.Ordinal));
    }

    /// <summary>
    /// The loader never follows a symbolic link, so a linked file is not in the corpus. Counting
    /// it as present would suppress the missing-file finding for a file no worker can read.
    /// </summary>
    [Fact]
    public void AFileBehindASymlinkIsNotCountedAsPresent()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/dotnet/dotnet.standards.md", Standard("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", Skills("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.arbiter-rules.yml", "rules: []\n")
            .Write("outside/dotnet.rules.md", Rules("dotnet"));
        File.CreateSymbolicLink(
            Path.Combine(fixture.Root, "6-Docs", "standards", "dotnet", "dotnet.rules.md"),
            Path.Combine(fixture.Root, "outside", "dotnet.rules.md"));

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.MissingFolderFile
            && i.Message.Contains("dotnet.rules.md", StringComparison.Ordinal));
    }

    private static string RoleRules(string scope) => Rules(scope).Replace(
        "technology: " + scope, "role: " + scope, StringComparison.Ordinal);

    private static string RoleStandard(string scope) =>
        $"""
        ---
        id: standards/{scope}
        title: {scope} standard
        doc-type: coding-standard
        status: current
        role: {scope}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # {scope}

        {SixHeadings}
        """;

    [Fact]
    public void ADeclaredRoleFolderIsCheckedTheSameWay()
    {
        using DocFixture fixture = new DocFixture().WithCatalog()
            .Write("6-Docs/standards/reviewer/reviewer.standards.md", RoleStandard("reviewer"))
            .Write("6-Docs/standards/reviewer/reviewer.rules.md", RoleRules("reviewer"))
            .Write("6-Docs/standards/reviewer/reviewer.arbiter-rules.yml", "rules: []\n");

        DiagnosticReport report = fixture.Validate(DotnetDeclared);

        Assert.Contains(report.Items, i =>
            i.Code == DocSpecValidator.MissingFolderFile
            && i.Message.Contains("reviewer.skills.md", StringComparison.Ordinal));
    }
}
