using KyberWeave.Core.Squad.Validation;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Integration.Tests.Squad;

/// <summary>
/// Proves on the real disk that a symbolic link whose target is gone is refused rather than skipped, at the places the
/// companion skills lister must never read through.
/// </summary>
public sealed class SquadCompanionSkillsLinkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void DanglingLinkAtTheCompanionDirectoryIsRefused()
    {
        string product = Path.Combine(_temp.Path, "repo", "products", "kyber-squad");
        Directory.CreateDirectory(product);
        Directory.CreateDirectory(Path.Combine(_temp.Path, "repo", ".apm"));
        Directory.CreateSymbolicLink(Path.Combine(_temp.Path, "repo", ".apm", "skills"), Path.Combine(_temp.Path, "gone"));

        _ = Assert.Throws<SquadSourceValidationException>(() => RealSquadServices.CompanionSkills().ResolveRoot(product));
    }

    [Fact]
    public void DanglingLinkAtASkillFileIsRefused()
    {
        string product = Path.Combine(_temp.Path, "repo", "products", "kyber-squad");
        string skill = Path.Combine(_temp.Path, "repo", ".apm", "skills", "linked");
        Directory.CreateDirectory(product);
        Directory.CreateDirectory(skill);
        File.CreateSymbolicLink(Path.Combine(skill, "SKILL.md"), Path.Combine(_temp.Path, "gone.md"));

        _ = Assert.Throws<SquadSourceValidationException>(() =>
            RealSquadServices.CompanionSkills().Collect(product, Path.Combine(_temp.Path, "repo", ".apm", "skills")));
    }

    [Fact]
    public void RealSkillFileIsListedWhereALinkedOneIsRefused()
    {
        string product = Path.Combine(_temp.Path, "repo", "products", "kyber-squad");
        string skill = Path.Combine(_temp.Path, "repo", ".apm", "skills", "plain");
        Directory.CreateDirectory(product);
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "plain");

        IReadOnlyList<(string RelativePath, string SourcePath)> listed =
            RealSquadServices.CompanionSkills().Collect(product, Path.Combine(_temp.Path, "repo", ".apm", "skills"));

        Assert.Equal(["skills/plain/SKILL.md"], listed.Select(file => file.RelativePath));
    }
}
