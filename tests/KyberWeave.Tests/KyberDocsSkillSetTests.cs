using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Tests.Corpus;
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
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly string[] RetiredApmSkillDirs =
    [
        "kyber-weave-docs",
        "doc-corpus-integrity-review"
    ];

    private static readonly Regex LiteralDocsPathRegex =
        new(@"(?<![\w<.-])docs/", RegexOptions.Compiled);

    private static string ApmSkillsRoot => ".apm/skills";

    private static string ProductRoot => "products/kyber-squad";

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
        string skillFile = $"{ApmSkillsRoot}/{skillName}/SKILL.md";

        Assert.True(Corpus.Exists(skillFile), $"Kyber-Docs skill '{skillName}' is missing: {skillFile}.");
        Assert.Contains($"name: {skillName}", Corpus.ReadText(skillFile), StringComparison.Ordinal);
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
                Corpus.DirectoryExists($"{ApmSkillsRoot}/{retired}"),
                $"Retired Kyber-Docs skill directory '{retired}' must be removed from .apm/skills.");
        }

        Assert.False(
            Corpus.DirectoryExists($"{ApmSkillsRoot}/second-brain"),
            "A 'second-brain' directory must not survive under .apm/skills.");
    }

    /// <summary>
    /// <c>second-brain</c> leaves Squad entirely: its source directory is deleted and the
    /// loaded Squad source carries no skill by that name.
    /// </summary>
    [Fact]
    public void SecondBrainIsGoneFromSquadSource()
    {
        SquadSource source = SquadSourceLoader.Load(Path.Combine(Corpus.Root, ProductRoot));

        Assert.False(
            Corpus.DirectoryExists($"{ProductRoot}/skills/second-brain"),
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
        SquadSource source = SquadSourceLoader.Load(Path.Combine(Corpus.Root, ProductRoot));
        string[] kyberDocsDirs = Corpus.EnumerateDirectories($"{ProductRoot}/skills")
            .Where(directory => Path.GetFileName(directory).StartsWith("kyber-docs-", StringComparison.Ordinal))
            .ToArray();

        Assert.DoesNotContain(
            source.Skills,
            skill => skill.Name.StartsWith("kyber-docs-", StringComparison.Ordinal));
        Assert.Empty(kyberDocsDirs);
    }

    /// <summary>
    /// The onboarding-and-assessment skill names the <c>docs-dev</c> role that remediates. The
    /// report states and the owner-started assessment mode are graded from the prose rule catalog
    /// (<c>onboarding-assessment-mode</c>).
    /// </summary>
    [Fact]
    public void OnboardingSkillNamesTheRemediator()
    {
        string skill = Corpus.ReadText($"{ApmSkillsRoot}/kyber-docs-onboarding-and-assessment/SKILL.md");

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
        string skillRoot = $"{ApmSkillsRoot}/{skillName}";
        string[] files = Corpus.EnumerateFiles(skillRoot, "*.md")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string text = Corpus.ReadText(file);
            Assert.False(
                LiteralDocsPathRegex.IsMatch(text),
                $"Kyber-Docs skill '{skillName}' embeds a literal docs/ path in " +
                $"'{file[(skillRoot.Length + 1)..]}'; resolve it through the Config Reg instead.");
        }
    }
}
