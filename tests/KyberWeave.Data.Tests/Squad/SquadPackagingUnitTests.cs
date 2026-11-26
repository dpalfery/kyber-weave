using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using KyberWeave.Data.Squad.Packaging;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Proves the Squad source locator and the companion skills lister through the in-memory file system alone: the
/// checkout is a few files in memory, so no test here reads a real directory.
/// </summary>
public sealed class SquadPackagingUnitTests
{
    private const string Repo = "/work/repo";
    private const string Product = "/work/repo/products/kyber-squad";

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/work/repo");

    public SquadPackagingUnitTests() => _paths = new FakePathResolver(_files);

    private SquadPackSourceLocator Locator => new(_files, _paths, _environment);

    private SquadCompanionSkills Companions => new(_files, _paths, _environment);

    [Fact]
    public void CheckoutWithTheSolutionAndTheManifestResolvesToTheProductDirectory()
    {
        WriteFile($"{Repo}/KyberWeave.sln", "");
        WriteFile($"{Product}/squad.yml", "x");

        Assert.Equal(Product, Locator.Resolve(Repo));
    }

    [Fact]
    public void RelativeWorkingDirectoryIsResolvedAgainstTheCurrentDirectory()
    {
        WriteFile($"{Repo}/KyberWeave.sln", "");
        WriteFile($"{Product}/squad.yml", "x");

        Assert.Equal(Product, Locator.Resolve("."));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void CheckoutMissingEitherMarkerDoesNotResolve(bool solution, bool manifest)
    {
        _files.CreateDirectory(Repo);
        if (solution)
        {
            WriteFile($"{Repo}/KyberWeave.sln", "");
        }

        if (manifest)
        {
            WriteFile($"{Product}/squad.yml", "x");
        }

        Assert.Null(Locator.Resolve(Repo));
    }

    [Fact]
    public void ParentDirectoriesAreNeverSearched()
    {
        WriteFile($"{Repo}/KyberWeave.sln", "");
        WriteFile($"{Product}/squad.yml", "x");
        _files.CreateDirectory($"{Repo}/src");

        Assert.Null(Locator.Resolve($"{Repo}/src"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/work/missing")]
    public void BlankOrMissingDirectoryDoesNotResolve(string directory)
    {
        Assert.Null(Locator.Resolve(directory));
    }

    [Fact]
    public void CompanionRootIsTheApmSkillsDirectoryOfTheRepositoryThatHoldsTheProduct()
    {
        _files.CreateDirectory($"{Repo}/.apm/skills");
        _files.CreateDirectory(Product);

        Assert.Equal($"{Repo}/.apm/skills", Companions.ResolveRoot(Product));
    }

    [Fact]
    public void RootThatIsNotShapedLikeTheProductHasNoCompanionRoot()
    {
        _files.CreateDirectory($"{Repo}/.apm/skills");
        _files.CreateDirectory("/work/other/squad");

        Assert.Null(Companions.ResolveRoot("/work/other/squad"));
    }

    [Fact]
    public void RepositoryWithoutCompanionSkillsHasNoCompanionRoot()
    {
        _files.CreateDirectory(Product);

        Assert.Null(Companions.ResolveRoot(Product));
    }

    [Theory]
    [InlineData(".apm")]
    [InlineData(".apm/skills")]
    public void LinkedCompanionDirectoryIsRefusedNotSkipped(string linked)
    {
        _files.CreateDirectory(Product);
        _files.CreateDirectory("/elsewhere/skills");
        if (linked == ".apm")
        {
            _files.CreateDirectoryLink($"{Repo}/.apm", "/elsewhere");
        }
        else
        {
            _files.CreateDirectory($"{Repo}/.apm");
            _files.CreateDirectoryLink($"{Repo}/.apm/skills", "/elsewhere/skills");
        }

        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => Companions.ResolveRoot(Product));

        Assert.Contains(linked, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DanglingLinkAtTheCompanionDirectoryIsRefusedToo()
    {
        _files.CreateDirectory(Product);
        _files.CreateDirectory($"{Repo}/.apm");
        _files.CreateDirectoryLink($"{Repo}/.apm/skills", "/elsewhere/gone");

        _ = Assert.Throws<SquadSourceValidationException>(() => Companions.ResolveRoot(Product));
    }

    [Fact]
    public void EveryFileOfEverySkillIsListedUnderItsSquadTreePathInOrdinalOrder()
    {
        WriteSkill("zeta", ("SKILL.md", "z"), ("refs/a.md", "a"));
        WriteSkill("alpha", ("SKILL.md", "a"));
        _files.CreateDirectory(Product);

        IReadOnlyList<(string RelativePath, string SourcePath)> listed =
            Companions.Collect(Product, $"{Repo}/.apm/skills");

        Assert.Equal(
            ["skills/alpha/SKILL.md", "skills/zeta/SKILL.md", "skills/zeta/refs/a.md"],
            listed.Select(file => file.RelativePath));
        Assert.Equal($"{Repo}/.apm/skills/zeta/refs/a.md", listed[^1].SourcePath);
    }

    [Fact]
    public void NoCompanionRootListsNothing()
    {
        Assert.Empty(Companions.Collect(Product, null));
    }

    [Fact]
    public void DirectoryWithoutASkillFileIsNotASkill()
    {
        WriteSkill("kept", ("SKILL.md", "k"));
        WriteFile($"{Repo}/.apm/skills/notes/readme.md", "n");

        IReadOnlyList<(string RelativePath, string SourcePath)> listed =
            Companions.Collect(Product, $"{Repo}/.apm/skills");

        Assert.Equal(["skills/kept/SKILL.md"], listed.Select(file => file.RelativePath));
    }

    [Fact]
    public void CompanionNamedLikeASquadSkillIgnoringCaseIsRefused()
    {
        WriteSkill("Task-Gate", ("SKILL.md", "t"));
        _files.CreateDirectory($"{Product}/skills/task-gate");

        SquadSourceValidationException failure = Assert.Throws<SquadSourceValidationException>(() =>
            Companions.Collect(Product, $"{Repo}/.apm/skills"));

        Assert.Contains("clashes with the Squad skill", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionNamedLikeASquadAgentIsRefused()
    {
        WriteSkill("conductor", ("SKILL.md", "c"));
        WriteFile($"{Product}/agents/Conductor.md", "agent");

        SquadSourceValidationException failure = Assert.Throws<SquadSourceValidationException>(() =>
            Companions.Collect(Product, $"{Repo}/.apm/skills"));

        Assert.Contains("clashes with the Squad agent", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkInsideASkillIsRefusedAndNoPartialListIsReturned()
    {
        WriteSkill("linked", ("SKILL.md", "l"));
        _files.CreateFileLink($"{Repo}/.apm/skills/linked/extra.md", "/elsewhere/extra.md");
        _files.CreateDirectory(Product);

        SquadSourceValidationException failure = Assert.Throws<SquadSourceValidationException>(() =>
            Companions.Collect(Product, $"{Repo}/.apm/skills"));

        Assert.Contains(".apm/skills/linked/extra.md", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkedSkillFileIsRefusedNotSkipped()
    {
        _files.CreateDirectory($"{Repo}/.apm/skills/linked");
        _files.CreateFileLink($"{Repo}/.apm/skills/linked/SKILL.md", "/elsewhere/SKILL.md");
        _files.CreateDirectory(Product);

        _ = Assert.Throws<SquadSourceValidationException>(() => Companions.Collect(Product, $"{Repo}/.apm/skills"));
    }

    private void WriteSkill(string name, params (string Path, string Content)[] files)
    {
        foreach ((string path, string content) in files)
        {
            WriteFile($"{Repo}/.apm/skills/{name}/{path}", content);
        }
    }

    private void WriteFile(string path, string content)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }
}
