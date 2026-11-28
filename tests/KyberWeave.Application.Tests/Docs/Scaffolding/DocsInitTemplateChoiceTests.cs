using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
/// Onboarding and <c>docs init</c> install only the template folders the operator names.
/// A file the host already has is left byte-for-byte unchanged and reported, and a declared
/// technology with no shipped template gets the same four files as stubs.
/// </summary>
public class DocsInitTemplateChoiceTests
{
    private static readonly IKyberStandardsTemplates Templates = ShippedKyberStandards.Templates;

    private readonly MemoryDocsHost _host = new();

    private string PathOf(string relative) => _host.InRepo(relative);

    private IServiceProvider Container => _host.Services();

    private IDocsScaffoldingService Scaffolding => Container.GetRequiredService<IDocsScaffoldingService>();

    private KyberWeaveConfig LoadConfig() =>
        Container.GetRequiredService<IKyberWeaveConfigRepository>().Load(_host.Repo);

    private string Today =>
        _host.Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task NamingTwoTechnologiesInstallsExactlyThoseFoldersAndTheSharedRulesFile()
    {
        ScaffoldResult result = await Scaffolding.ScaffoldAsync(_host.Repo, standards: ["csharp", "python"]);

        Assert.True(_host.Files.FileExists(PathOf("docs/standards/csharp/csharp.standards.md")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/csharp/csharp.rules.md")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/csharp/csharp.skills.yml")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/csharp/csharp.arbiter-rules.yml")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/python/python.standards.md")));
        Assert.False(_host.Files.DirectoryExists(PathOf("docs/standards/typescript")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/shared-rules.md")));

        KyberWeaveConfig config = LoadConfig();
        Assert.Equal(["csharp", "python"], config.Ontology.Technologies);
        Assert.Empty(config.Ontology.Roles);
    }

    [Fact]
    public async Task DocsInitWritesSkillsYmlAndCopiesTheAssignedShippedSkills()
    {
        await Scaffolding.ScaffoldAsync(_host.Repo, standards: ["tauri"]);

        string list = _host.Files.ReadAllText(PathOf("docs/standards/tauri/tauri.skills.yml"));
        Assert.Contains("tauri-v2-reference", list, StringComparison.Ordinal);

        // tauri builds on rust, so the parent's skill comes with it.
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/skills/tauri-v2-reference/SKILL.md")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/skills/rust-cross-build/SKILL.md")));
        Assert.False(_host.Files.DirectoryExists(PathOf("docs/standards/skills/go-gates")));
    }

    [Fact]
    public async Task DocsInitDoesNotOverwriteAnExistingRepositorySkillFolder()
    {
        const string mine = "---\nname: tauri-v2-reference\ndescription: Mine.\n---\n\nMine.\n";
        string skill = PathOf("docs/standards/skills/tauri-v2-reference/SKILL.md");
        _host.Files.CreateDirectory(Path.GetDirectoryName(skill)!);
        _host.Files.WriteAllText(skill, mine);

        await Scaffolding.ScaffoldAsync(_host.Repo, standards: ["tauri"], force: true);

        Assert.Equal(mine, _host.Files.ReadAllText(skill));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/skills/rust-cross-build/SKILL.md")));
    }

    [Fact]
    public async Task DocsInitWritesNoSkillsMd()
    {
        await Scaffolding.ScaffoldAsync(_host.Repo, kyberStandards: true, standards: ["architect"]);

        Assert.Empty(_host.Files.EnumerateFiles(PathOf("docs/standards"), "*.skills.md", recursive: true));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/csharp/csharp.skills.yml")));
        Assert.Equal("[]\n", _host.Files.ReadAllText(PathOf("docs/standards/architect/architect.skills.yml")));
    }

    [Fact]
    public async Task AnExistingFileIsUnchangedAndReported()
    {
        const string custom = "---\nid: standards/csharp\ntitle: Mine\n---\n\n# Mine\n";
        string path = PathOf("docs/standards/csharp/csharp.standards.md");
        _host.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _host.Files.WriteAllText(path, custom);

        ScaffoldResult result = await Scaffolding.ScaffoldAsync(_host.Repo, standards: ["csharp"]);

        Assert.Equal(custom, _host.Files.ReadAllText(path));
        Assert.Contains(result.Files, file =>
            file.RelativePath == "docs/standards/csharp/csharp.standards.md"
            && file.Outcome == ScaffoldOutcome.Skipped);
    }

    /// <summary>
    /// A host whose standard is still <c>README.md</c> already has a standard with an id. A stub
    /// beside it would carry the same id, which validation then reports as a duplicate, so
    /// <c>docs init</c> leaves the standard name to the migration command.
    /// </summary>
    [Fact]
    public async Task ALegacyReadmeStandardIsNotJoinedByAScaffoldedStub()
    {
        const string legacy = "---\nid: standards/csharp\ntitle: Mine\n---\n\n# Mine\n";
        string path = PathOf("docs/standards/csharp/README.md");
        _host.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _host.Files.WriteAllText(path, legacy);

        ScaffoldResult result = await Scaffolding.ScaffoldAsync(_host.Repo, standards: ["csharp"]);

        Assert.False(_host.Files.FileExists(PathOf("docs/standards/csharp/csharp.standards.md")));
        Assert.Equal(legacy, _host.Files.ReadAllText(path));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/csharp/csharp.rules.md")));
        Assert.Contains(result.Files, file =>
            file.RelativePath == "docs/standards/csharp/csharp.standards.md"
            && file.Outcome == ScaffoldOutcome.Skipped
            && file.Note is not null
            && file.Note.Contains("docs standards migrate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeclaredTechnologyWithNoTemplateGetsFourStubs()
    {
        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        _host.Files.CreateDirectory(Path.GetDirectoryName(configPath)!);
        _host.Files.WriteAllText(configPath, """
            ontology:
              docs-root: docs
              technologies:
                - elixir
            """);

        await Scaffolding.ScaffoldAsync(_host.Repo);

        Assert.True(_host.Files.FileExists(PathOf("docs/standards/elixir/elixir.standards.md")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/elixir/elixir.rules.md")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/elixir/elixir.skills.yml")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/elixir/elixir.arbiter-rules.yml")));
        Assert.Contains(
            "technology: elixir",
            _host.Files.ReadAllText(PathOf("docs/standards/elixir/elixir.standards.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamingARoleInstallsItUnderOntologyRoles()
    {
        await Scaffolding.ScaffoldAsync(_host.Repo, standards: ["reviewer"]);

        Assert.True(_host.Files.FileExists(PathOf("docs/standards/reviewer/reviewer.standards.md")));
        Assert.Contains(
            "role: reviewer",
            _host.Files.ReadAllText(PathOf("docs/standards/reviewer/reviewer.standards.md")),
            StringComparison.Ordinal);

        KyberWeaveConfig config = LoadConfig();
        Assert.Equal(["reviewer"], config.Ontology.Roles);
        Assert.Empty(config.Ontology.Technologies);
    }

    [Fact]
    public async Task NamingATechnologyThatShipsATemplateInstallsTheTemplateNotAStub()
    {

        await Scaffolding.ScaffoldAsync(_host.Repo, standards: ["csharp"]);

        Assert.Equal(
            Templates.RenderPart(
                "csharp", KyberStandardsTemplates.Part.Standard, "unassigned", Today),
            _host.Files.ReadAllText(PathOf("docs/standards/csharp/csharp.standards.md")));
    }

    /// <summary>
    /// A delivery-platform folder ships a template too, and <c>--kyber-standards</c> covers every
    /// folder it installs: a host that declares <c>github</c> gets github's own four files, not the
    /// generic stubs.
    /// </summary>
    [Fact]
    public async Task KyberStandardsRendersAPlatformTemplateForADeclaredPlatform()
    {
        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        _host.Files.CreateDirectory(Path.GetDirectoryName(configPath)!);
        _host.Files.WriteAllText(configPath, """
            ontology:
              docs-root: docs
              technologies:
                - github
            """);

        await Scaffolding.ScaffoldAsync(_host.Repo, kyberStandards: true);

        Assert.Equal(
            Templates.RenderPart(
                "github", KyberStandardsTemplates.Part.Standard, "unassigned", Today),
            _host.Files.ReadAllText(PathOf("docs/standards/github/github.standards.md")));
    }

    /// <summary>
    /// <c>--standards</c> names folders, and only the folders it names get a shipped template. A
    /// technology the host already declared keeps the stub its folder would have been given without
    /// the flag, so asking for one folder does not restyle every other one.
    /// </summary>
    [Fact]
    public async Task NamingOneTechnologyDoesNotGiveAPreexistingTechnologyItsTemplate()
    {
        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        _host.Files.CreateDirectory(Path.GetDirectoryName(configPath)!);
        _host.Files.WriteAllText(configPath, """
            ontology:
              docs-root: docs
              technologies:
                - csharp
            """);
        string folder = PathOf("docs/standards/csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(
            Path.Combine(folder, "csharp.standards.md"),
            "---\nid: standards/csharp\ntitle: Mine\n---\n\n# Mine\n");

        await Scaffolding.ScaffoldAsync(_host.Repo, standards: ["python"]);

        string rules = _host.Files.ReadAllText(PathOf("docs/standards/csharp/csharp.rules.md"));
        Assert.NotEqual(
            Templates.RenderPart("csharp", KyberStandardsTemplates.Part.Rules, "unassigned", Today),
            rules);
        Assert.Contains("technology: csharp", rules, StringComparison.Ordinal);
        Assert.Contains("doc-type: rule", rules, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asking for every shipped technology and for one role folder in the same run has to do
    /// both: the second choice is not a replacement for the first.
    /// </summary>
    [Fact]
    public async Task NamingARoleBesideKyberStandardsInstallsBoth()
    {
        await Scaffolding.ScaffoldAsync(_host.Repo, kyberStandards: true, standards: ["reviewer"]);

        Assert.True(_host.Files.FileExists(PathOf("docs/standards/reviewer/reviewer.standards.md")));
        Assert.True(_host.Files.FileExists(PathOf("docs/standards/csharp/csharp.standards.md")));

        KyberWeaveConfig config = LoadConfig();
        Assert.Contains("reviewer", config.Ontology.Roles);
    }

    [Fact]
    public async Task AnUnknownNameIsRefusedWithTheNearestMatch()
    {
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Scaffolding.ScaffoldAsync(_host.Repo, standards: ["cshar"]));

        Assert.Contains("csharp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KyberStandardsStillInstallsEveryTechnologyTemplate()
    {
        await Scaffolding.ScaffoldAsync(_host.Repo, kyberStandards: true);

        foreach (string technology in KyberStandardsTemplates.All)
        {
            Assert.True(
                _host.Files.FileExists(PathOf($"docs/standards/{technology}/{technology}.standards.md")),
                $"{technology} was not installed.");
        }
    }
}
