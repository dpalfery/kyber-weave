using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Shipped shape of the two step-1 rules added for tasks 3.11 and 3.13.
/// <c>KW-ARB-TRIM-001</c> asks, for each candidate section of a document, whether the section is relevant to the
/// task. It runs for-each over <c>section.candidates</c> at a 0.9 floor. <c>KW-ARB-COMMENT-001</c> classifies one bot
/// review comment as <c>real</c>, <c>nitpick</c> or <c>already-fixed</c> at a 0.85 floor. Both ship in shadow.
/// RED: the two rules are not in <c>default-rules.yml</c> yet.
/// </summary>
/// <remarks>
/// The for-each clause is pinned as text in the shipped file because the expansion itself is covered by
/// <c>ArbiterForEachExpansionTests</c>; this file pins only that the shipped rule declares it.
/// </remarks>
public sealed class ArbiterSectionAndCommentRuleTests
{
    private const string TrimRule = "KW-ARB-TRIM-001";
    private const string CommentRule = "KW-ARB-COMMENT-001";

    private static readonly string ShippedRulesPath =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Core", "Arbiter", "Rules", "default-rules.yml");

    private static ArbiterRule Shipped(string id) =>
        Assert.Single(ArbiterConfig.ProductDefaults.Rules, rule => string.Equals(rule.Id, id, StringComparison.Ordinal));

    /// <summary>The section rule is a step-1 choice between relevant and not-relevant, floored at 0.9, and shipped in shadow.</summary>
    [Fact]
    public void TheSectionTrimRuleAsksRelevanceAtTheNinetyFloorInShadow()
    {
        ArbiterRule trim = Shipped(TrimRule);

        Assert.NotNull(trim.Ask);
        Assert.Equal("choice", trim.Ask.Type);
        Assert.Equal(0.9, trim.Ask.ConfidenceAtLeast);
        Assert.Equal(["relevant", "not-relevant"], trim.Answers);
        Assert.True(trim.Shadow);
    }

    /// <summary>The section rule expands once per candidate, so the shipped text must say so.</summary>
    [Fact]
    public void TheSectionTrimRuleIsDeclaredForEachSectionCandidate()
    {
        string[] lines = File.ReadAllLines(ShippedRulesPath);
        int start = Array.FindIndex(lines, line => line.Trim() == $"- id: {TrimRule}");
        Assert.True(start >= 0, $"{TrimRule} is not declared in default-rules.yml.");
        int end = Array.FindIndex(lines, start + 1, line => line.TrimStart().StartsWith("- id: ", StringComparison.Ordinal));
        string block = string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);

        Assert.Contains("for-each: section.candidates", block, StringComparison.Ordinal);
    }

    /// <summary>The comment rule classifies into exactly the three classes the triage service returns, at a 0.85 floor, in shadow.</summary>
    [Fact]
    public void TheCommentRuleClassifiesIntoThreeAnswersAtTheEightyFiveFloorInShadow()
    {
        ArbiterRule comment = Shipped(CommentRule);

        Assert.NotNull(comment.Ask);
        Assert.Equal("choice", comment.Ask.Type);
        Assert.Equal(0.85, comment.Ask.ConfidenceAtLeast);
        Assert.Equal(["real", "nitpick", "already-fixed"], comment.Answers);
        Assert.True(comment.Shadow);
    }

    /// <summary>
    /// The fail-safe answer is real. It must map to the neutral <c>keep</c> effect, not to escalate or deny, so a
    /// comment the rule cannot classify leaves the decision where the other rules put it.
    /// </summary>
    [Fact]
    public void TheCommentRuleKeepsTheDispatchForTheFailSafeRealAnswer()
    {
        ArbiterRule comment = Shipped(CommentRule);

        Assert.Equal(RuleEffects.Keep, comment.Effects["real"]);
    }

    /// <summary>The two rules are appended after the existing ids, so no shipped id is renumbered or moved.</summary>
    [Fact]
    public void TheNewRulesAreAppendedAfterTheShortcutAndSameFailureRules()
    {
        string[] ids = ArbiterConfig.ProductDefaults.Rules.Select(rule => rule.Id).Distinct().ToArray();

        Assert.Equal("KW-ARB-FAILURE-001", ids[^5]);
        Assert.Equal(TrimRule, ids[^4]);
        Assert.Equal(CommentRule, ids[^3]);
        Assert.Equal("KW-ARB-CHECKOUT-001", ids[^2]);
        Assert.Equal("KW-ARB-MODE-002", ids[^1]);
    }
}
