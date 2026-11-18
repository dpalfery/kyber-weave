using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The shipped conductor rules file declares <c>KW-ARB-PLANREAD-001</c>; the rule's behaviour is pinned over fixture
/// facts by <c>ArbiterPlanReadRuleTests</c>.
/// </summary>
public sealed class ArbiterPlanReadRuleShippedFileTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string PlanReadRule = "KW-ARB-PLANREAD-001";

    private const string ConductorRulesPath =
        "products/kyber-squad/standards/conductor/conductor.arbiter-rules.yml";

    /// <summary>
    /// Ids are permanent, so the new rule joins the file without renumbering any of the
    /// eighteen, and the count in the file's own header is part of the shipped contract: a
    /// reader checking how many rules ship must not have to count them.
    /// </summary>
    [Fact]
    public void TheShippedFileDeclaresTheRuleAsTheTwentySixth()
    {
        Assert.True(
            Corpus.Exists(ConductorRulesPath),
            $"the conductor rules file must live at '{ConductorRulesPath}'.");
        string[] ids =
        [
            .. CorpusLines.Split(Corpus.ReadText(ConductorRulesPath))
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("- id: ", StringComparison.Ordinal))
                .Select(line => line["- id: ".Length..].Trim())
        ];

        Assert.Equal(21, ids.Length);
        Assert.Contains(PlanReadRule, ids);
        Assert.Equal("delegate", Assert.Single(ArbiterConfigDefaults.Product.Rules, rule => rule.Id == PlanReadRule).Trigger);
    }
}
