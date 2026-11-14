using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins that the CI skill gate covers every skill folder under <c>.apm/skills</c>.
/// </summary>
/// <remarks>
/// The gate step reads its skill list from the folder, so validate, lint and scan cover a new skill
/// without an edit. Code scanning needs one upload step per skill, because each upload carries its own
/// category; this suite fails when a skill folder has no such step, or when the loop goes back to a
/// hand-kept list. It reads files only.
/// </remarks>
public sealed class CiSkillGateCoverageTests
{
    /// <summary>The gate step enumerates <c>.apm/skills/*/</c> instead of listing skills by hand.</summary>
    [Fact]
    public void SkillGateEnumeratesTheSkillsFolder()
    {
        string ci = ReadCi();

        Assert.Contains("for dir in .apm/skills/*/", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("skills_dirs=(.apm/skills/", ci, StringComparison.Ordinal);
    }

    /// <summary>Every skill folder has a SARIF upload step for the file the gate writes for it.</summary>
    [Fact]
    public void EverySkillFolderHasASarifUploadStep()
    {
        string ci = ReadCi();
        string skillsRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, ".apm", "skills");
        string[] skills = Directory.GetDirectories(skillsRoot).Select(d => Path.GetFileName(d)!).Order().ToArray();
        Assert.NotEmpty(skills);

        List<string> missing = skills
            .Where(skill => !ci.Contains($"sarif_file: kyber-weave-sarif/skills-_apm_skills_{skill}.sarif", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"ci.yml has no SARIF upload step for: {string.Join(", ", missing)}. Add one per skill to the skill-docs-gate job.");
    }

    private static string ReadCi() =>
        File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, ".github", "workflows", "ci.yml"));
}
