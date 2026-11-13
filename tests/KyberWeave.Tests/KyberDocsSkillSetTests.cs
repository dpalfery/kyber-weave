using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The Kyber-Docs skill set: <c>second-brain</c> moved out of Squad to
/// <c>.apm/skills/kyber-docs-onboarding-and-assessment</c>, the two remaining Kyber-Weave
/// documentation skills take <c>kyber-docs-*</c> names, no Kyber-Docs skill lives in Squad,
/// the onboarding skill names its report states, and no skill embeds a literal <c>docs/</c> path.
/// </summary>
public sealed class KyberDocsSkillSetTests
{
    private static readonly string[] RetiredApmSkillDirs =
    [
        "kyber-weave-docs",
        "doc-corpus-integrity-review"
    ];

    private static readonly Regex LiteralDocsPathRegex =
        new(@"(?<![\w<.-])docs/", RegexOptions.Compiled);

    private static string ToolRoot => KyberWeaveTestPaths.ToolRoot;

    private static string ApmSkillsRoot => Path.Combine(ToolRoot, ".apm", "skills");

    private static string ProductRoot => Path.Combine(ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Each Kyber-Docs skill ships a <c>SKILL.md</c> whose frontmatter name matches its
    /// directory: the directory is the install identity APM deploys.
    /// </summary>
    [Theory]
    [InlineData("kyber-docs-authoring")]
    [InlineData("kyber-docs-integrity-review")]
    [InlineData("kyber-docs-onboarding-and-assessment")]
    public void KyberDocsSkillShipsSkillMdWithMatchingName(string skillName)
    {
        string skillFile = Path.Combine(ApmSkillsRoot, skillName, "SKILL.md");

        Assert.True(File.Exists(skillFile), $"Kyber-Docs skill '{skillName}' is missing: {skillFile}.");
        Assert.Contains($"name: {skillName}", File.ReadAllText(skillFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// The rename leaves no retired directory behind: neither the authoring skill's old
    /// home nor the integrity-review skill's old home may survive under
    /// <c>.apm/skills</c>, and no <c>second-brain</c> directory may be left there either.
    /// </summary>
    [Fact]
    public void RetiredApmSkillDirsAreGone()
    {
        foreach (string retired in RetiredApmSkillDirs)
        {
            Assert.False(
                Directory.Exists(Path.Combine(ApmSkillsRoot, retired)),
                $"Retired Kyber-Docs skill directory '{retired}' must be removed from .apm/skills.");
        }

        Assert.False(
            Directory.Exists(Path.Combine(ApmSkillsRoot, "second-brain")),
            "A 'second-brain' directory must not survive under .apm/skills.");
    }

    /// <summary>
    /// <c>second-brain</c> leaves Squad entirely: its source directory is deleted and the
    /// loaded Squad source carries no skill by that name.
    /// </summary>
    [Fact]
    public void SecondBrainIsGoneFromSquadSource()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.False(
            Directory.Exists(Path.Combine(ProductRoot, "skills", "second-brain")),
            "products/kyber-squad/skills/second-brain must be removed.");
        Assert.DoesNotContain(source.Skills, skill => skill.Name == "second-brain");
        Assert.DoesNotContain("second-brain", source.Bundle.SkillNames);
    }

    /// <summary>
    /// Kyber-Docs skills are never duplicated into the Squad source tree: no <c>kyber-docs-*</c>
    /// directory or skill identity may live under <c>products/kyber-squad</c>. Their single source
    /// is <c>.apm/skills</c>, and Squad adds them from there when it builds the tree, so they still
    /// reach every Squad deployment. This guard passes on the phase-1 tree by design.
    /// </summary>
    [Fact]
    public void NoKyberDocsSkillLivesInSquad()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string[] kyberDocsDirs = Directory.EnumerateDirectories(
            Path.Combine(ProductRoot, "skills"), "kyber-docs-*", SearchOption.TopDirectoryOnly).ToArray();

        Assert.DoesNotContain(
            source.Skills,
            skill => skill.Name.StartsWith("kyber-docs-", StringComparison.Ordinal));
        Assert.Empty(kyberDocsDirs);
    }

    /// <summary>
    /// The onboarding-and-assessment skill names the per-plan/spec report states the owner
    /// triages (<c>harvested</c>, <c>partial</c>, <c>not harvested</c>) and the <c>docs-dev</c>
    /// role that remediates. The owner-started assessment mode itself is graded from the prose
    /// rule catalog (<c>onboarding-assessment-mode</c>).
    /// </summary>
    [Fact]
    public void OnboardingSkillNamesTheAssessmentReportStatesAndRemediator()
    {
        string skill = File.ReadAllText(
            Path.Combine(ApmSkillsRoot, "kyber-docs-onboarding-and-assessment", "SKILL.md"));

        Assert.Contains("harvested", skill, StringComparison.Ordinal);
        Assert.Contains("partial", skill, StringComparison.Ordinal);
        Assert.Contains("not harvested", skill, StringComparison.Ordinal);
        Assert.Contains("docs-dev", skill, StringComparison.Ordinal);
    }

    /// <summary>
    /// Portable Kyber-Docs skills never embed a literal <c>docs/</c> path: every
    /// documentation location resolves through Config Reg properties such as
    /// <c>&lt;docs-root&gt;</c>, so a host that mounts documentation elsewhere keeps
    /// working. Same shape as the harvest contract's portable-files guard.
    /// </summary>
    [Theory]
    [InlineData("kyber-docs-authoring")]
    [InlineData("kyber-docs-integrity-review")]
    [InlineData("kyber-docs-onboarding-and-assessment")]
    public void NoLiteralDocsPathsInKyberDocsSkills(string skillName)
    {
        string skillRoot = Path.Combine(ApmSkillsRoot, skillName);
        string[] files = Directory.EnumerateFiles(skillRoot, "*.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            Assert.False(
                LiteralDocsPathRegex.IsMatch(text),
                $"Kyber-Docs skill '{skillName}' embeds a literal docs/ path in " +
                $"'{Path.GetRelativePath(skillRoot, file)}'; resolve it through the Config Reg instead.");
        }
    }
}
