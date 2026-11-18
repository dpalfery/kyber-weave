using KyberWeave.Application.Skills;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Skills;

/// <summary>Skill loading over the in-memory file system, with a relative path read against the fake working directory.</summary>
public sealed class SkillFileReaderTests
{
    private const string Work = "/kw/work";
    private const string SkillText = "---\nname: password-reset\ndescription: Use to reset a corporate password.\n---\n\nbody\n";

    private readonly InMemoryFileSystem _files = new();
    private readonly SkillFileReader _reader;

    public SkillFileReaderTests() =>
        _reader = new SkillFileReader(
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: Work));

    [Fact]
    public void ARelativeSkillFileIsReadAgainstTheWorkingDirectory()
    {
        _files.CreateDirectory("/kw/work/skills/password-reset");
        _files.WriteAllText("/kw/work/skills/password-reset/SKILL.md", SkillText);

        Skill skill = _reader.ReadFile("skills/password-reset/SKILL.md");

        Assert.Equal("password-reset", skill.Frontmatter.Name);
        Assert.Equal("/kw/work/skills/password-reset", skill.DirectoryPath);
    }

    [Fact]
    public void ATreeOfSkillsLoadsOneResultPerSkillFile()
    {
        _files.CreateDirectory("/kw/work/skills/password-reset");
        _files.WriteAllText("/kw/work/skills/password-reset/SKILL.md", SkillText);

        SkillLoadResult result = Assert.Single(_reader.Load("/kw/work/skills"));

        Assert.True(result.Success);
    }

    [Fact]
    public void AMissingPathIsReportedNotThrown()
    {
        SkillLoadResult result = Assert.Single(_reader.Load("/kw/work/absent"));

        Assert.False(result.Success);
    }
}
