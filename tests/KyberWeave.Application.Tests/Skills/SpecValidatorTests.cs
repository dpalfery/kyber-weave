using KyberWeave.Application.Skills;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Skills;

public class SpecValidatorTests
{
    private readonly InMemoryFileSystem _files = new();

    private Skill Make(string content)
    {
        string dir = Path.Combine(_files.Root, "skill-x");
        return new SkillFileReader(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: _files.Root))
            .Read(content, Path.Combine(dir, "SKILL.md"), dir);
    }

    [Fact]
    public void FlagsMissingDescription()
    {
        Skill skill = Make("---\nname: skill-x\n---\nbody");
        IEnumerable<string> codes = SpecValidator.Validate(skill, _files.DirectoryExists(skill.DirectoryPath)).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-005", codes);
    }

    [Fact]
    public void FlagsInvalidNameCharacters()
    {
        Skill skill = Make("---\nname: Bad_Name\ndescription: d\n---\nbody");
        IEnumerable<string> codes = SpecValidator.Validate(skill, _files.DirectoryExists(skill.DirectoryPath)).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-003", codes);
    }

    [Fact]
    public void FlagsAngleBracketsInFrontmatter()
    {
        Skill skill = Make("---\nname: skill-x\ndescription: use <tool> now\n---\nbody");
        IEnumerable<string> codes = SpecValidator.Validate(skill, _files.DirectoryExists(skill.DirectoryPath)).Select(d => d.Code);
        Assert.Contains("KW-SKILL-SPEC-008", codes);
    }

    [Fact]
    public void ValidSkillHasNoErrors()
    {
        Skill skill = Make("---\nname: skill-x\ndescription: Use to do X. Use when Y. Do NOT use for Z.\n---\nbody");
        List<Diagnostic> diags = SpecValidator.Validate(skill, _files.DirectoryExists(skill.DirectoryPath)).ToList();
        Assert.DoesNotContain(diags, d => d.Severity is Severity.Error or Severity.Critical);
    }
}
