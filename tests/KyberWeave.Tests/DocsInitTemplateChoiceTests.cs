using KyberWeave.Tests.Configuration;

using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Onboarding and <c>docs init</c> install only the template folders the operator names.
/// A file the host already has is left byte-for-byte unchanged and reported, and a declared
/// technology with no shipped template gets the same four files as stubs.
/// </summary>
public class DocsInitTemplateChoiceTests : IDisposable
{
    private static readonly IKyberStandardsTemplates Templates = ShippedKyberStandards.Templates;

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private string PathOf(string relative) =>
        Path.Combine(_temp.Path, relative.Replace('/', Path.DirectorySeparatorChar));

    private static IDocsScaffoldingService Scaffolding => DocsTestCommands.Scaffolding;

    [Fact]
    public void NamingTwoTechnologiesInstallsExactlyThoseFoldersAndTheSharedRulesFile()
    {
        ScaffoldResult result = Scaffolding.Scaffold(_temp.Path, standards: ["csharp", "python"]);

        Assert.True(File.Exists(PathOf("docs/standards/csharp/csharp.standards.md")));
        Assert.True(File.Exists(PathOf("docs/standards/csharp/csharp.rules.md")));
        Assert.True(File.Exists(PathOf("docs/standards/csharp/csharp.skills.md")));
        Assert.True(File.Exists(PathOf("docs/standards/csharp/csharp.arbiter-rules.yml")));
        Assert.True(File.Exists(PathOf("docs/standards/python/python.standards.md")));
        Assert.False(Directory.Exists(PathOf("docs/standards/typescript")));
        Assert.True(File.Exists(PathOf("docs/standards/shared-rules.md")));

        KyberWeaveConfig config = ConfigFileReader.Load(_temp.Path);
        Assert.Equal(["csharp", "python"], config.Ontology.Technologies);
        Assert.Empty(config.Ontology.Roles);
    }

    [Fact]
    public void AnExistingFileIsUnchangedAndReported()
    {
        const string custom = "---\nid: standards/csharp\ntitle: Mine\n---\n\n# Mine\n";
        string path = PathOf("docs/standards/csharp/csharp.standards.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, custom);

        ScaffoldResult result = Scaffolding.Scaffold(_temp.Path, standards: ["csharp"]);

        Assert.Equal(custom, File.ReadAllText(path));
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
    public void ALegacyReadmeStandardIsNotJoinedByAScaffoldedStub()
    {
        const string legacy = "---\nid: standards/csharp\ntitle: Mine\n---\n\n# Mine\n";
        string path = PathOf("docs/standards/csharp/README.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, legacy);

        ScaffoldResult result = Scaffolding.Scaffold(_temp.Path, standards: ["csharp"]);

        Assert.False(File.Exists(PathOf("docs/standards/csharp/csharp.standards.md")));
        Assert.Equal(legacy, File.ReadAllText(path));
        Assert.True(File.Exists(PathOf("docs/standards/csharp/csharp.rules.md")));
        Assert.Contains(result.Files, file =>
            file.RelativePath == "docs/standards/csharp/csharp.standards.md"
            && file.Outcome == ScaffoldOutcome.Skipped
            && file.Note is not null
            && file.Note.Contains("docs standards migrate", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeclaredTechnologyWithNoTemplateGetsFourStubs()
    {
        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            ontology:
              docs-root: docs
              technologies:
                - elixir
            """);

        Scaffolding.Scaffold(_temp.Path);

        Assert.True(File.Exists(PathOf("docs/standards/elixir/elixir.standards.md")));
        Assert.True(File.Exists(PathOf("docs/standards/elixir/elixir.rules.md")));
        Assert.True(File.Exists(PathOf("docs/standards/elixir/elixir.skills.md")));
        Assert.True(File.Exists(PathOf("docs/standards/elixir/elixir.arbiter-rules.yml")));
        Assert.Contains(
            "technology: elixir",
            File.ReadAllText(PathOf("docs/standards/elixir/elixir.standards.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NamingARoleInstallsItUnderOntologyRoles()
    {
        Scaffolding.Scaffold(_temp.Path, standards: ["reviewer"]);

        Assert.True(File.Exists(PathOf("docs/standards/reviewer/reviewer.standards.md")));
        Assert.Contains(
            "role: reviewer",
            File.ReadAllText(PathOf("docs/standards/reviewer/reviewer.standards.md")),
            StringComparison.Ordinal);

        KyberWeaveConfig config = ConfigFileReader.Load(_temp.Path);
        Assert.Equal(["reviewer"], config.Ontology.Roles);
        Assert.Empty(config.Ontology.Technologies);
    }

    [Fact]
    public void NamingATechnologyThatShipsATemplateInstallsTheTemplateNotAStub()
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        Scaffolding.Scaffold(_temp.Path, standards: ["csharp"]);

        Assert.Equal(
            Templates.RenderPart(
                "csharp", KyberStandardsTemplates.Part.Standard, "unassigned", today),
            File.ReadAllText(PathOf("docs/standards/csharp/csharp.standards.md")));
    }

    /// <summary>
    /// A delivery-platform folder ships a template too, and <c>--kyber-standards</c> covers every
    /// folder it installs: a host that declares <c>github</c> gets github's own four files, not the
    /// generic stubs.
    /// </summary>
    [Fact]
    public void KyberStandardsRendersAPlatformTemplateForADeclaredPlatform()
    {
        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            ontology:
              docs-root: docs
              technologies:
                - github
            """);

        Scaffolding.Scaffold(_temp.Path, kyberStandards: true);

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(
            Templates.RenderPart(
                "github", KyberStandardsTemplates.Part.Standard, "unassigned", today),
            File.ReadAllText(PathOf("docs/standards/github/github.standards.md")));
    }

    /// <summary>
    /// <c>--standards</c> names folders, and only the folders it names get a shipped template. A
    /// technology the host already declared keeps the stub its folder would have been given without
    /// the flag, so asking for one folder does not restyle every other one.
    /// </summary>
    [Fact]
    public void NamingOneTechnologyDoesNotGiveAPreexistingTechnologyItsTemplate()
    {
        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            ontology:
              docs-root: docs
              technologies:
                - csharp
            """);
        string folder = PathOf("docs/standards/csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "csharp.standards.md"),
            "---\nid: standards/csharp\ntitle: Mine\n---\n\n# Mine\n");

        Scaffolding.Scaffold(_temp.Path, standards: ["python"]);

        string today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string rules = File.ReadAllText(PathOf("docs/standards/csharp/csharp.rules.md"));
        Assert.NotEqual(
            Templates.RenderPart("csharp", KyberStandardsTemplates.Part.Rules, "unassigned", today),
            rules);
        Assert.Contains("technology: csharp", rules, StringComparison.Ordinal);
        Assert.Contains("doc-type: rule", rules, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asking for every shipped technology and for one role folder in the same run has to do
    /// both: the second choice is not a replacement for the first.
    /// </summary>
    [Fact]
    public void NamingARoleBesideKyberStandardsInstallsBoth()
    {
        Scaffolding.Scaffold(_temp.Path, kyberStandards: true, standards: ["reviewer"]);

        Assert.True(File.Exists(PathOf("docs/standards/reviewer/reviewer.standards.md")));
        Assert.True(File.Exists(PathOf("docs/standards/csharp/csharp.standards.md")));

        KyberWeaveConfig config = ConfigFileReader.Load(_temp.Path);
        Assert.Contains("reviewer", config.Ontology.Roles);
    }

    [Fact]
    public void AnUnknownNameIsRefusedWithTheNearestMatch()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            Scaffolding.Scaffold(_temp.Path, standards: ["cshar"]));

        Assert.Contains("csharp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KyberStandardsStillInstallsEveryTechnologyTemplate()
    {
        Scaffolding.Scaffold(_temp.Path, kyberStandards: true);

        foreach (string technology in KyberStandardsTemplates.All)
        {
            Assert.True(
                File.Exists(PathOf($"docs/standards/{technology}/{technology}.standards.md")),
                $"{technology} was not installed.");
        }
    }
}
