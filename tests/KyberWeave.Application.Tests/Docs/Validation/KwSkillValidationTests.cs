using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.KwSkills;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>
/// The KW-DOC-SKILL rules: what a scope's <c>&lt;scope&gt;.skills.yml</c> list may hold, what a repository
/// pool <c>SKILL.md</c> must declare, that a pool folder without a real <c>SKILL.md</c> is an error, that a
/// legacy <c>.skills.md</c> is only a warning, and that no technology or role may be named <c>skills</c>.
/// Each case yields exactly one finding with a fix hint.
/// </summary>
public sealed class KwSkillValidationTests
{
    private const string ConfigYaml = """
        ontology:
          docs-root: 6-Docs
          technologies: [dotnet]
        """;

    private const string ShippedManifest = """
        skills:
          - name: shipped-one
            files:
              - SKILL.md
        """;

    private const string SixHeadings = """
        ## Naming and layout

        ## Coding standards

        ## Testing

        ## Commands

        ## Security and data handling

        ## Done checklist
        """;

    private static readonly OntologyConfig Dotnet = new() { Technologies = ["dotnet"] };

    private static string Standard(string scope) =>
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

        {SixHeadings}
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

    private static string LegacySkills(string scope) =>
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

    /// <summary>A dotnet folder that holds every file it needs except a list written as <paramref name="skillsYml"/>.</summary>
    private static DocFixture Scope(string skillsYml) =>
        DocFixture.CreateInMemory().WithCatalog()
            .Write("6-Docs/standards/dotnet/dotnet.standards.md", Standard("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.rules.md", Rules("dotnet"))
            .Write("6-Docs/standards/dotnet/dotnet.arbiter-rules.yml", "rules: []\n")
            .Write("6-Docs/standards/dotnet/dotnet.skills.yml", skillsYml);

    /// <summary>The catalog over the fixture's own file system, with the given shipped pool.</summary>
    private static IKwSkillCatalog Catalog(DocFixture fixture, FakeEmbeddedResources resources) =>
        new KwSkillCatalog(
            fixture.Memory!,
            fixture.Paths,
            fixture.Environment,
            resources,
            fixture.Root,
            KyberWeaveConfigAssembler.FromYaml(ConfigYaml));

    private static DiagnosticReport Validate(DocFixture fixture, OntologyConfig ontology, IKwSkillCatalog? catalog = null) =>
        new DocSpecValidator(fixture.Root, ontology, fixture.Store, fixture.Paths, fixture.Environment, catalog)
            .Validate(fixture.Loader(ontology).Load());

    /// <summary>Asserts the report holds exactly one finding, with the code and severity given and a fix hint.</summary>
    private static Diagnostic Only(DiagnosticReport report, string code, Severity severity)
    {
        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal(code, finding.Code);
        Assert.Equal(severity, finding.Severity);
        Assert.False(string.IsNullOrWhiteSpace(finding.Hint), "every KW skill finding carries a fix hint");
        return finding;
    }

    [Fact]
    public void ASkillsYmlThatIsAMapIsKwDocSkill001()
    {
        using DocFixture fixture = Scope("go-gates: yes\n");

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillListMalformed, Severity.Error);

        Assert.Contains("dotnet.skills.yml", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkillsYmlThatIsAScalarIsKwDocSkill001()
    {
        using DocFixture fixture = Scope("go-gates\n");

        Only(Validate(fixture, Dotnet), DocSpecValidator.SkillListMalformed, Severity.Error);
    }

    [Fact]
    public void ASkillsYmlThatIsAListOfNonStringsIsKwDocSkill001()
    {
        using DocFixture fixture = Scope("- [go-gates]\n- tauri-ref\n");

        Only(Validate(fixture, Dotnet), DocSpecValidator.SkillListMalformed, Severity.Error);
    }

    [Fact]
    public void AListedSkillWithNoFolderIsKwDocSkill002()
    {
        using DocFixture fixture = Scope("- ghost-skill\n");

        Diagnostic finding = Only(
            Validate(fixture, Dotnet, Catalog(fixture, new FakeEmbeddedResources())),
            DocSpecValidator.SkillListUnknownName,
            Severity.Error);

        Assert.Contains("ghost-skill", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListedNameThatOnlyTheShippedPoolHasIsNotReported()
    {
        using DocFixture fixture = Scope("- shipped-one\n");
        FakeEmbeddedResources resources = new FakeEmbeddedResources()
            .Add("KwSkills/skills.manifest.yml", ShippedManifest);

        DiagnosticReport report = Validate(fixture, Dotnet, Catalog(fixture, resources));

        Assert.Empty(report.Items);
    }

    [Fact]
    public void AnEmptyListIsValid()
    {
        using DocFixture fixture = Scope("[]\n");

        Assert.Empty(Validate(fixture, Dotnet).Items);
    }

    [Fact]
    public void ASkillMdWithNoNameIsKwDocSkill003()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("6-Docs/standards/skills/go-gates/SKILL.md", "---\ndescription: Run the gates.\n---\n\nBODY\n");

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolFrontmatter, Severity.Error);

        Assert.Contains("name", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkillMdWithNoDescriptionIsKwDocSkill003()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("6-Docs/standards/skills/go-gates/SKILL.md", "---\nname: go-gates\n---\n\nBODY\n");

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolFrontmatter, Severity.Error);

        Assert.Contains("description", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameThatDiffersFromItsFolderIsKwDocSkill004()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("6-Docs/standards/skills/go-gates/SKILL.md", "---\nname: go-tools\ndescription: Run the gates.\n---\n\nBODY\n");

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolNameMismatch, Severity.Error);

        Assert.Contains("go-tools", finding.Message, StringComparison.Ordinal);
        Assert.Contains("go-gates", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegacySkillsMdIsKwDocSkill005Warning()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("6-Docs/standards/dotnet/dotnet.skills.md", LegacySkills("dotnet"));

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.LegacySkillsFile, Severity.Warning);

        Assert.Contains("dotnet.skills.md", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderNamedSkillsDeclaredAsATechnologyIsKwDocSkill006()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog();

        Only(
            Validate(fixture, new OntologyConfig { Technologies = ["skills"] }),
            DocSpecValidator.ReservedSkillsScope,
            Severity.Error);
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("../x")]
    public void AnInvalidListedNameIsKwDocSkill002AndNeverReachesTheFileSystem(string name)
    {
        using DocFixture fixture = Scope($"- \"{name}\"\n");

        Diagnostic finding = Only(
            Validate(fixture, Dotnet, Catalog(fixture, new FakeEmbeddedResources())),
            DocSpecValidator.SkillListUnknownName,
            Severity.Error);

        Assert.Contains(name, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyScalarEntryIsKwDocSkill001()
    {
        using DocFixture fixture = Scope("- \"\"\n");

        Only(Validate(fixture, Dotnet), DocSpecValidator.SkillListMalformed, Severity.Error);
    }

    [Fact]
    public void AWhitespaceOnlyScalarEntryIsKwDocSkill001()
    {
        using DocFixture fixture = Scope("- \"  \"\n");

        Only(Validate(fixture, Dotnet), DocSpecValidator.SkillListMalformed, Severity.Error);
    }

    [Fact]
    public void AnUnreadableListFileIsKwDocSkill001NotACrash()
    {
        using DocFixture fixture = Scope("[]\n");
        fixture.Memory!.FailWhen(
            operation => operation.Name == "ReadAllText"
                && operation.Path is not null
                && operation.Path.EndsWith("dotnet.skills.yml", StringComparison.Ordinal),
            new IOException("denied"));

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillListMalformed, Severity.Error);

        Assert.Contains("could not be read", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadablePoolSkillMdIsKwDocSkill003NotACrash()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("6-Docs/standards/skills/go-gates/SKILL.md", "---\nname: go-gates\ndescription: Run.\n---\n\nBODY\n");
        fixture.Memory!.FailWhen(
            operation => operation.Name == "ReadAllText"
                && operation.Path is not null
                && operation.Path.EndsWith("SKILL.md", StringComparison.Ordinal),
            new IOException("denied"));

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolFrontmatter, Severity.Error);

        Assert.Contains("could not be read", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnprobeablePoolLinkIsKwDocSkill007NotACrash()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("6-Docs/standards/skills/go-gates/SKILL.md", "---\nname: go-gates\ndescription: Run.\n---\n\nBODY\n");
        fixture.Memory!.FailWhen(
            operation => operation.Name == "GetLinkTarget"
                && operation.Path is not null
                && operation.Path.EndsWith("skills", StringComparison.Ordinal),
            new IOException("denied"));

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolMalformedFolder, Severity.Error);

        Assert.Contains("could not be checked", finding.Message, StringComparison.Ordinal);
        Assert.Contains("denied", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnprobeableSkillLinkIsKwDocSkill007NotACrash()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("6-Docs/standards/skills/go-gates/SKILL.md", "---\nname: go-gates\ndescription: Run.\n---\n\nBODY\n");
        fixture.Memory!.FailWhen(
            operation => operation.Name == "GetLinkTarget"
                && operation.Path is not null
                && operation.Path.EndsWith("SKILL.md", StringComparison.Ordinal),
            new IOException("denied"));

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolMalformedFolder, Severity.Error);

        Assert.Contains("go-gates", finding.Message, StringComparison.Ordinal);
        Assert.Contains("could not be checked", finding.Message, StringComparison.Ordinal);
        Assert.Contains("denied", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedPoolSkillMdFrontmatterIsKwDocSkill003WithTheParserReason()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("6-Docs/standards/skills/go-gates/SKILL.md", "---\nname: [unclosed\n---\n\nBODY\n");

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolFrontmatter, Severity.Error);

        Assert.Contains("no readable frontmatter", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APoolFolderWithNoSkillMdIsKwDocSkill007()
    {
        using DocFixture fixture = Scope("[]\n");
        fixture.Memory!.CreateDirectory(Path.Combine(fixture.Root, "6-Docs", "standards", "skills", "go-gates"));

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolMalformedFolder, Severity.Error);

        Assert.Contains("go-gates", finding.Message, StringComparison.Ordinal);
        Assert.Contains("SKILL.md", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASymlinkedSkillMdIsKwDocSkill007AndIsNeverFollowed()
    {
        // The link's target holds no frontmatter, so a followed link would report KW-DOC-SKILL-003;
        // a single KW-DOC-SKILL-007 finding is the proof the link was reported without being read.
        using DocFixture fixture = Scope("[]\n")
            .Write("elsewhere/go-gates/SKILL.md", "no frontmatter at all\n");
        fixture.Memory!.CreateDirectory(Path.Combine(fixture.Root, "6-Docs", "standards", "skills", "go-gates"));
        fixture.Memory.CreateFileLink(
            Path.Combine(fixture.Root, "6-Docs", "standards", "skills", "go-gates", "SKILL.md"),
            Path.Combine(fixture.Root, "elsewhere", "go-gates", "SKILL.md"));

        Diagnostic finding = Only(Validate(fixture, Dotnet), DocSpecValidator.SkillPoolMalformedFolder, Severity.Error);

        Assert.Contains("symbolic link", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkedSkillFolderInThePoolIsNotCheckedAndALinkedPoolIsNotRead()
    {
        using DocFixture fixture = Scope("[]\n")
            .Write("elsewhere/go-gates/SKILL.md", "no frontmatter at all\n");
        string root = fixture.Root;
        fixture.Memory!.CreateDirectory(Path.Combine(root, "6-Docs", "standards", "skills"));
        fixture.Memory.CreateDirectoryLink(
            Path.Combine(root, "6-Docs", "standards", "skills", "go-gates"), Path.Combine(root, "elsewhere", "go-gates"));

        Assert.Empty(Validate(fixture, Dotnet).Items);

        // A pool that is itself a link is skipped whole.
        using DocFixture linkedPool = Scope("[]\n")
            .Write("elsewhere/pool/go-gates/SKILL.md", "no frontmatter at all\n");
        linkedPool.Memory!.CreateDirectoryLink(
            Path.Combine(linkedPool.Root, "6-Docs", "standards", "skills"), Path.Combine(linkedPool.Root, "elsewhere", "pool"));

        Assert.Empty(Validate(linkedPool, Dotnet).Items);
    }
}
