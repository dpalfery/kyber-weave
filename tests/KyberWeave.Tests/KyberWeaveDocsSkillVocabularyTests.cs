using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The authoring skill once published a doc-type list that omitted <c>todo</c> and
/// <c>coding-standard</c>, then told the agent to use <c>reference</c> when nothing
/// fitted. A coding standard labelled that way validates, and nothing reports that
/// it will never resolve as a standard.
/// </summary>
public sealed class KyberWeaveDocsSkillVocabularyTests
{
    private static string SkillRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, ".apm", "skills", "kyber-weave-docs");

    [Fact]
    public void TheSkillReadsTheClosedDocTypeSetFromTheOntology()
    {
        string skill = File.ReadAllText(Path.Combine(SkillRoot, "SKILL.md"));

        Assert.DoesNotContain("the answer is `reference`", skill, StringComparison.Ordinal);
        Assert.DoesNotContain("is one of:", skill, StringComparison.Ordinal);
        Assert.Contains("<documentation-ontology>", skill, StringComparison.Ordinal);
        Assert.Contains("`todo`", skill, StringComparison.Ordinal);
        Assert.Contains("`coding-standard`", skill, StringComparison.Ordinal);
        Assert.Contains("standards/<technology>/README.md", skill, StringComparison.Ordinal);
        Assert.Contains("ontology.technologies", skill, StringComparison.Ordinal);
        Assert.Contains("KW-DOC-SPEC-007", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void RetrofitDoesNotSendAStandardPathToReference()
    {
        string retrofit = File.ReadAllText(Path.Combine(SkillRoot, "references", "retrofit.md"));

        Assert.Contains("`standards/<technology>/` | `coding-standard`", retrofit, StringComparison.Ordinal);
        Assert.Contains("`todo/` | `todo`", retrofit, StringComparison.Ordinal);
        Assert.DoesNotContain("is the correct fallback", retrofit, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoadedRuleReferenceCoversAMisplacedTechnology()
    {
        string rules = File.ReadAllText(Path.Combine(SkillRoot, "references", "rules.md"));

        Assert.Contains("### `KW-DOC-SPEC-007`", rules, StringComparison.Ordinal);
        Assert.Contains("ontology.technologies", rules, StringComparison.Ordinal);
    }
}
