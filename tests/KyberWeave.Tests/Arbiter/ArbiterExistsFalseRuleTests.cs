using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Every shipped rule carrying an <c>exists: false</c> clause, driven through the real
/// path: a real event, the real classifier, the real fact builder and the real engine,
/// against the embedded <c>default-rules.yml</c>. Hand-fed fact sets proved nothing here
/// -- the builder pre-fills each declared fact with null, so the bug was only visible
/// end to end.
/// </summary>
public sealed class ArbiterExistsFalseRuleTests
{
    private const string PromptWithoutPlan =
        "KYBER-ARBITER: true\nTASK: T3\n\nImplement the thing.";

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = new ArbiterConfig
        {
            Enabled = true,
            Rules = [.. ArbiterConfig.ProductDefaults.Rules],
        },
    };

    /// <summary>The shipped rules that gate on a fact being absent.</summary>
    private static IReadOnlyList<ArbiterRule> RulesWithAnExistsFalseClause(KyberWeaveConfig config) =>
        [.. config.Arbiter.Rules.Where(rule =>
            rule.Enabled
            && rule.Decide is not null
            && rule.Decide.Any(clause => MentionsExistsFalse(clause.When)))];

    private static bool MentionsExistsFalse(RulePredicate predicate)
    {
        if (predicate.Operator == RuleOperator.Exists
            && predicate.Operand?.LiteralValue is false)
        {
            return true;
        }

        return predicate.Children.Any(MentionsExistsFalse);
    }

    [Fact]
    public void TheShippedRulesGatedOnAnAbsentFactAreTheOnesTheCatalogueNames()
    {
        // Pinning the set: a rule that gains or loses an exists:false clause has to come
        // back through this file.
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "KW-ARB-SCOPE-002",
                "KW-ARB-READY-001",
                "KW-ARB-OWNER-001",
                "KW-ARB-MODE-001",
                "KW-ARB-DIFF-001",
                "KW-ARB-PLANNER-DIFF-001",
                "KW-ARB-PLANNER-001",
                "KW-ARB-PLANREAD-001",
                "KW-ARB-READONLY-001",
                "KW-ARB-GATE-REPORT-001",
                "KW-ARB-LADDER-001",
                "KW-ARB-DOCIMPACT-001",
                "KW-ARB-FAILURE-001",
            },
            RulesWithAnExistsFalseClause(Config()).Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void ADispatchWithNoPlanFileLeavesPlan001ToTheModelAndTheTaskRulesAllow()
    {
        // Whether a dispatch names a plan is a reading of prose (ADR 0072): step 0 cannot say no, so PLAN-001 is
        // undecidable and asks. The rules that need a plan task answer their no-task guard, which allows; a
        // null-valued fact has to count as absent for those guards to fire at all.
        KyberWeaveConfig config = Config();
        ArbiterFactSet facts = BuildDelegateFacts(config);
        ArbiterRule plan = Assert.Single(config.Arbiter.Rules, r => r.Id == "KW-ARB-PLAN-001");

        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(plan, facts));
        Assert.NotNull(plan.Ask);
        foreach (string id in new[] { "KW-ARB-SCOPE-002", "KW-ARB-READY-001", "KW-ARB-OWNER-001", "KW-ARB-MODE-001" })
        {
            ArbiterRule rule = Assert.Single(config.Arbiter.Rules, r => r.Id == id);
            Assert.Equal("no-task", RuleEngine.Decide(rule, facts));
            Assert.Equal(RuleEffects.Allow, rule.Effects["no-task"]);
        }
    }

    [Fact]
    public void ADispatchWithNoMarkersLeavesPlanner001ToTheModel()
    {
        // A marker is an agent-written line; step 0 only recognises the exact forms and asks about the rest.
        KyberWeaveConfig config = Config();
        ArbiterRule rule = Assert.Single(config.Arbiter.Rules, r => r.Id == "KW-ARB-PLANNER-001");
        ArbiterFactSet facts = BuildDelegatePlannerFacts(config);

        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(rule, facts));
        Assert.NotNull(rule.Ask);
    }

    [Fact]
    public void EveryExistsFalseRuleReachesItsAbsentClauseOnTheRealPath()
    {
        // The general statement of the contract: a fact with no value is absent, so its
        // exists:false clause holds, and no shipped rule is silently dead.
        KyberWeaveConfig config = Config();
        ArbiterFactSet facts = BuildDelegateFacts(config);

        foreach (ArbiterRule rule in RulesWithAnExistsFalseClause(config))
        {
            foreach (RuleDecideClause clause in rule.Decide!)
            {
                // A clause that is itself the absence test; a compound clause also tests other facts.
                if (clause.When.Operator == RuleOperator.Exists && clause.When.Operand?.LiteralValue is false)
                {
                    Assert.True(
                        clause.When.Evaluate(facts),
                        $"{rule.Id} carries an exists:false clause that cannot match the facts "
                        + "a real dispatch produces.");
                }
            }
        }
    }

    [Fact]
    public void AnAbsentFactStillFailsEveryOtherOperator()
    {
        // The other half of the documented rule: treating a null value as absent must not
        // turn exists:false into a match for equals, in, matches, count or the combinators.
        ArbiterFactSet facts = BuildDelegateFacts(Config());

        Assert.False(RulePredicate.Equals("delegation.plan-file", RuleOperand.Literal("x")).Evaluate(facts));
        Assert.False(RulePredicate.In("delegation.plan-file", RuleOperand.Literal(new List<object> { "x" })).Evaluate(facts));
        Assert.False(RulePredicate.Matches("delegation.plan-file", RuleOperand.Literal("**")).Evaluate(facts));
        Assert.False(RulePredicate.Count("delegation.plan-file", 0).Evaluate(facts));
        Assert.True(RulePredicate.Exists("delegation.plan-file", false).Evaluate(facts));
        Assert.False(RulePredicate.Exists("delegation.plan-file", true).Evaluate(facts));
        Assert.True(RulePredicate.Not(RulePredicate.Exists("delegation.plan-file", true)).Evaluate(facts));
    }

    [Fact]
    public void AFactThatCarriesAValueIsStillPresent()
    {
        const string promptWithPlan = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nImplement.";
        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            new ArbiterEvent
            {
                Harness = "claude",
                Phase = "pre",
                Target = "csharp-dev",
                HarnessCaller = "conductor",
                IsDispatch = true,
                Prompt = promptWithPlan,
            },
            "csharp-dev",
            promptWithPlan);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, promptWithPlan, Config());

        Assert.True(RulePredicate.Exists("delegation.plan-file", true).Evaluate(facts));
        Assert.True(RulePredicate.Equals("delegation.plan-file", RuleOperand.Literal("docs/plans/plan.md")).Evaluate(facts));
        Assert.False(RulePredicate.Exists("delegation.plan-file", false).Evaluate(facts));
    }

    private static ArbiterFactSet BuildDelegateFacts(KyberWeaveConfig config)
    {
        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            new ArbiterEvent
            {
                Harness = "claude",
                Phase = "pre",
                Target = "csharp-dev",
                HarnessCaller = "conductor",
                IsDispatch = true,
                Prompt = PromptWithoutPlan,
            },
            "csharp-dev",
            PromptWithoutPlan);
        return TriggerFactBuilder.Build(classification, PromptWithoutPlan, config);
    }

    private static ArbiterFactSet BuildDelegatePlannerFacts(KyberWeaveConfig config)
    {
        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            new ArbiterEvent
            {
                Harness = "claude",
                Phase = "pre",
                Target = "architect",
                HarnessCaller = "conductor",
                IsDispatch = true,
                Prompt = PromptWithoutPlan,
            },
            "architect",
            PromptWithoutPlan);
        return TriggerFactBuilder.Build(classification, PromptWithoutPlan, config);
    }
}
