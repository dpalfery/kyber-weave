using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.1: the closed step-0 predicate set, first-match decide,
/// trigger families (effects, undecidable mapping, combination) and the step-1
/// short-circuit signal. RED: <see cref="RuleEngine"/> does not exist yet.
/// </summary>
public sealed class ArbiterRuleEngineTests
{
    private static ArbiterFactSet Facts(params (string Name, object? Value, ArbiterFactLabel Label)[] entries)
    {
        ArbiterFactSet set = new();
        foreach ((string name, object? value, ArbiterFactLabel label) in entries)
        {
            set = set.With(name, value, label);
        }

        return set;
    }

    private static ArbiterRule DecideRule(string id, string trigger, params RuleDecideClause[] clauses)
    {
        Dictionary<string, string> effects = new(StringComparer.Ordinal)
        {
            ["in-task-files"] = RuleEffects.Allow,
            ["beyond-files"] = RuleEffects.Allow,
            ["no-task-files"] = RuleEffects.Allow,
            ["not-in-plan"] = RuleEffects.Escalate,
        };
        return new ArbiterRule(
            id,
            trigger,
            "Does TASK name a plan task, and do the paths stay inside its files?",
            ["in-task-files", "beyond-files", "no-task-files", "not-in-plan"],
            effects,
            clauses);
    }

    // ---- exists ----

    [Fact]
    public void Exists_AbsentFact_SatisfiesExistsFalse()
    {
        RulePredicate when = RulePredicate.Exists("plan.task", false);
        Assert.True(when.Evaluate(new ArbiterFactSet()));
    }

    [Fact]
    public void Exists_AbsentFact_FailsExistsTrue()
    {
        RulePredicate when = RulePredicate.Exists("plan.task", true);
        Assert.False(when.Evaluate(new ArbiterFactSet()));
    }

    [Fact]
    public void Exists_PresentFact_MatchesExpectation()
    {
        ArbiterFactSet facts = Facts(("plan.task", "T3 text", ArbiterFactLabel.Asserted));
        Assert.True(RulePredicate.Exists("plan.task", true).Evaluate(facts));
        Assert.False(RulePredicate.Exists("plan.task", false).Evaluate(facts));
    }

    // ---- equals (literal and fact ref) ----

    [Fact]
    public void Equals_LiteralScalar_MatchesExactly()
    {
        ArbiterFactSet facts = Facts(("plan.task.kind", "code", ArbiterFactLabel.Derived));
        Assert.True(RulePredicate.Equals("plan.task.kind", RuleOperand.Literal("code")).Evaluate(facts));
        Assert.False(RulePredicate.Equals("plan.task.kind", RuleOperand.Literal("docs")).Evaluate(facts));
    }

    [Fact]
    public void Equals_FactRef_ComparesTwoFacts()
    {
        ArbiterFactSet facts = Facts(
            ("a", "x", ArbiterFactLabel.Asserted),
            ("b", "x", ArbiterFactLabel.Derived),
            ("c", "y", ArbiterFactLabel.Derived));
        Assert.True(RulePredicate.Equals("a", RuleOperand.Fact("b")).Evaluate(facts));
        Assert.False(RulePredicate.Equals("a", RuleOperand.Fact("c")).Evaluate(facts));
    }

    [Fact]
    public void Equals_AbsentFact_FailsEveryOperand()
    {
        ArbiterFactSet facts = Facts(("b", "x", ArbiterFactLabel.Derived));
        Assert.False(RulePredicate.Equals("missing", RuleOperand.Literal("x")).Evaluate(facts));
        Assert.False(RulePredicate.Equals("missing", RuleOperand.Fact("b")).Evaluate(facts));
        Assert.False(RulePredicate.Equals("b", RuleOperand.Fact("missing")).Evaluate(facts));
    }

    // ---- in ----

    [Fact]
    public void In_ScalarMembership_UsesOperandList()
    {
        ArbiterFactSet facts = Facts(("lens", "infra-workflow", ArbiterFactLabel.Asserted));
        Assert.True(RulePredicate.In("lens", RuleOperand.Literal(new List<object?> { "infra-workflow", "docs" })).Evaluate(facts));
        Assert.False(RulePredicate.In("lens", RuleOperand.Literal(new List<object?> { "docs" })).Evaluate(facts));
    }

    [Fact]
    public void In_AbsentFact_Fails()
    {
        Assert.False(RulePredicate.In("missing", RuleOperand.Literal(new List<object?> { "a" })).Evaluate(new ArbiterFactSet()));
    }

    // ---- matches (PathGlob) ----

    [Fact]
    public void Matches_PathList_UsesPathGlob()
    {
        ArbiterFactSet facts = Facts(
            ("changed", new List<object?> { "src/auth/login.cs" }, ArbiterFactLabel.Derived));
        Assert.True(RulePredicate.Matches("changed", RuleOperand.Literal("**/auth/**")).Evaluate(facts));
        Assert.False(RulePredicate.Matches("changed", RuleOperand.Literal("docs/**")).Evaluate(facts));
    }

    [Fact]
    public void Matches_AbsentFact_Fails()
    {
        Assert.False(RulePredicate.Matches("missing", RuleOperand.Literal("**")).Evaluate(new ArbiterFactSet()));
    }

    // ---- subset-of (PathGlob on path lists) ----

    [Fact]
    public void SubsetOf_PathsInsideTaskFiles_HoldsViaGlob()
    {
        ArbiterFactSet facts = Facts(
            ("delegation.paths", new List<object?> { "src/auth/a.cs" }, ArbiterFactLabel.Asserted),
            ("plan.task.files", new List<object?> { "src/auth/**" }, ArbiterFactLabel.Derived));
        Assert.True(RulePredicate.SubsetOf("delegation.paths", RuleOperand.Fact("plan.task.files")).Evaluate(facts));
    }

    [Fact]
    public void SubsetOf_PathOutsideTaskFiles_Fails()
    {
        ArbiterFactSet facts = Facts(
            ("delegation.paths", new List<object?> { "src/auth/a.cs", "docs/other.md" }, ArbiterFactLabel.Asserted),
            ("plan.task.files", new List<object?> { "src/auth/**" }, ArbiterFactLabel.Derived));
        Assert.False(RulePredicate.SubsetOf("delegation.paths", RuleOperand.Fact("plan.task.files")).Evaluate(facts));
    }

    [Fact]
    public void SubsetOf_EmptyList_IsSubsetOfAnything()
    {
        ArbiterFactSet facts = Facts(
            ("delegation.paths", new List<object?>(), ArbiterFactLabel.Asserted),
            ("plan.task.files", new List<object?> { "src/**" }, ArbiterFactLabel.Derived));
        Assert.True(RulePredicate.SubsetOf("delegation.paths", RuleOperand.Fact("plan.task.files")).Evaluate(facts));
    }

    [Fact]
    public void SubsetOf_AbsentFact_Fails()
    {
        ArbiterFactSet facts = Facts(
            ("plan.task.files", new List<object?> { "src/**" }, ArbiterFactLabel.Derived));
        Assert.False(RulePredicate.SubsetOf("missing", RuleOperand.Fact("plan.task.files")).Evaluate(facts));
    }

    // ---- intersects (PathGlob on path lists) ----

    [Fact]
    public void Intersects_SharedGlobCoverage_Holds()
    {
        ArbiterFactSet facts = Facts(
            ("changed", new List<object?> { "src/auth/a.cs", "docs/x.md" }, ArbiterFactLabel.Derived),
            ("reserved", new List<object?> { "**/auth/**" }, ArbiterFactLabel.Asserted));
        Assert.True(RulePredicate.Intersects("changed", RuleOperand.Fact("reserved")).Evaluate(facts));
    }

    [Fact]
    public void Intersects_NoOverlap_Fails()
    {
        ArbiterFactSet facts = Facts(
            ("changed", new List<object?> { "docs/x.md" }, ArbiterFactLabel.Derived),
            ("reserved", new List<object?> { "**/auth/**" }, ArbiterFactLabel.Asserted));
        Assert.False(RulePredicate.Intersects("changed", RuleOperand.Fact("reserved")).Evaluate(facts));
    }

    // ---- count ----

    [Fact]
    public void Count_EmptyList_MatchesZero()
    {
        ArbiterFactSet facts = Facts(
            ("plan.task.files", new List<object?>(), ArbiterFactLabel.Derived));
        Assert.True(RulePredicate.Count("plan.task.files", 0).Evaluate(facts));
        Assert.False(RulePredicate.Count("plan.task.files", 1).Evaluate(facts));
    }

    [Fact]
    public void Count_AbsentFact_FailsEvenForZero()
    {
        Assert.False(RulePredicate.Count("missing", 0).Evaluate(new ArbiterFactSet()));
    }

    // ---- all / any / not ----

    [Fact]
    public void All_RequiresEveryChild()
    {
        ArbiterFactSet facts = Facts(("a", "x", ArbiterFactLabel.Asserted));
        RulePredicate all = RulePredicate.All(
            RulePredicate.Exists("a", true),
            RulePredicate.Equals("a", RuleOperand.Literal("x")));
        Assert.True(all.Evaluate(facts));
        RulePredicate failing = RulePredicate.All(
            RulePredicate.Exists("a", true),
            RulePredicate.Equals("a", RuleOperand.Literal("y")));
        Assert.False(failing.Evaluate(facts));
    }

    [Fact]
    public void Any_RequiresOneChild()
    {
        ArbiterFactSet facts = Facts(("a", "x", ArbiterFactLabel.Asserted));
        Assert.True(RulePredicate.Any(
            RulePredicate.Equals("a", RuleOperand.Literal("y")),
            RulePredicate.Equals("a", RuleOperand.Literal("x"))).Evaluate(facts));
        Assert.False(RulePredicate.Any(
            RulePredicate.Equals("a", RuleOperand.Literal("y")),
            RulePredicate.Exists("missing", true)).Evaluate(facts));
    }

    [Fact]
    public void Not_NegatesChild()
    {
        ArbiterFactSet facts = Facts(("a", "x", ArbiterFactLabel.Asserted));
        Assert.True(RulePredicate.Not(RulePredicate.Exists("missing", true)).Evaluate(facts));
        Assert.False(RulePredicate.Not(RulePredicate.Exists("a", true)).Evaluate(facts));
    }

    // ---- first match wins / undecidable ----

    [Fact]
    public void Decide_FirstMatchingClause_Wins()
    {
        ArbiterRule rule = DecideRule(
            "KW-ARB-SCOPE-001",
            "delegate",
            new RuleDecideClause(RulePredicate.Exists("plan.task", false), "not-in-plan"),
            new RuleDecideClause(RulePredicate.Count("plan.task.files", 0), "no-task-files"),
            new RuleDecideClause(RulePredicate.SubsetOf("delegation.paths", RuleOperand.Fact("plan.task.files")), "in-task-files"),
            new RuleDecideClause(RulePredicate.Exists("delegation.paths", true), "beyond-files"));
        ArbiterFactSet facts = Facts(
            ("plan.task", "T3", ArbiterFactLabel.Asserted),
            ("plan.task.files", new List<object?>(), ArbiterFactLabel.Derived),
            ("delegation.paths", new List<object?> { "src/a.cs" }, ArbiterFactLabel.Asserted));
        Assert.Equal("no-task-files", RuleEngine.Decide(rule, facts));
    }

    [Fact]
    public void Decide_NoClauseMatches_AnswersUndecidable()
    {
        ArbiterRule rule = DecideRule(
            "KW-ARB-SCOPE-001",
            "delegate",
            new RuleDecideClause(RulePredicate.Exists("plan.task", false), "not-in-plan"));
        ArbiterFactSet facts = Facts(("plan.task", "T3", ArbiterFactLabel.Asserted));
        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(rule, facts));
    }

    [Fact]
    public void Decide_DisabledRule_AnswersUndecidable()
    {
        ArbiterRule rule = DecideRule(
            "KW-ARB-SCOPE-001",
            "delegate",
            new RuleDecideClause(RulePredicate.Exists("plan.task", true), "not-in-plan")) with
        {
            Enabled = false,
        };
        ArbiterFactSet facts = Facts(("plan.task", "T3", ArbiterFactLabel.Asserted));
        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(rule, facts));
    }

    // ---- step-1 short-circuit ----

    [Fact]
    public void ShouldAsk_RuleWithBothDecideAndAsk_AsksOnlyWhenUndecidable()
    {
        RuleAsk ask = new("choice", new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = "plan.task.text" });
        ArbiterRule rule = DecideRule(
            "KW-ARB-LENS-001",
            "lens.spawn",
            new RuleDecideClause(RulePredicate.Exists("lens", true), "not-applicable")) with
        {
            Ask = ask,
        };
        Assert.True(rule.ShouldAsk(RuleEngine.Undecidable));
        Assert.False(rule.ShouldAsk("not-applicable"));
    }

    [Fact]
    public void EvaluateStep0_EscalateEffect_ShortCircuitsStep1()
    {
        ArbiterRule rule = DecideRule(
            "KW-ARB-SCOPE-001",
            "delegate",
            new RuleDecideClause(RulePredicate.Exists("plan.task", false), "not-in-plan"));
        ArbiterOutcome outcome = RuleEngine.EvaluateStep0([rule], new ArbiterFactSet(), "delegate");
        Assert.Equal(RuleEffects.Escalate, outcome.CombinedEffect);
        Assert.True(outcome.Step0ShortCircuitsStep1);
    }

    [Fact]
    public void EvaluateStep0_AllowAnswer_DoesNotShortCircuit()
    {
        ArbiterRule rule = DecideRule(
            "KW-ARB-SCOPE-001",
            "delegate",
            new RuleDecideClause(RulePredicate.Exists("plan.task", true), "in-task-files"));
        ArbiterFactSet facts = Facts(("plan.task", "T3", ArbiterFactLabel.Asserted));
        ArbiterOutcome outcome = RuleEngine.EvaluateStep0([rule], facts, "delegate");
        Assert.Equal("in-task-files", outcome.RuleAnswers[0].Answer);
        Assert.Equal(RuleEffects.Allow, outcome.CombinedEffect);
        Assert.False(outcome.Step0ShortCircuitsStep1);
    }

    [Fact]
    public void EvaluateStep0_UndecidableOnConductor_EscalatesAndShortCircuits()
    {
        ArbiterRule rule = DecideRule(
            "KW-ARB-SCOPE-001",
            "delegate",
            new RuleDecideClause(RulePredicate.Exists("missing", true), "in-task-files"));
        ArbiterFactSet facts = Facts(("plan.task", "T3", ArbiterFactLabel.Asserted));
        ArbiterOutcome outcome = RuleEngine.EvaluateStep0([rule], facts, "delegate");
        Assert.Equal(RuleEngine.Undecidable, outcome.RuleAnswers[0].Answer);
        Assert.Equal(RuleEffects.Escalate, outcome.CombinedEffect);
        Assert.True(outcome.Step0ShortCircuitsStep1);
    }

    // ---- trigger families ----

    [Theory]
    [InlineData("delegate")]
    [InlineData("delegate.returned")]
    [InlineData("delegate.planner")]
    [InlineData("delegate.planner.returned")]
    public void ForTrigger_ConductorTriggers_MapToConductorFamily(string trigger)
    {
        Assert.Equal(ArbiterTriggerFamily.Conductor, ArbiterTriggerFamilies.ForTrigger(trigger));
    }

    [Theory]
    [InlineData("investigate")]
    [InlineData("investigate.returned")]
    public void ForTrigger_InvestigateTriggers_MapToInvestigateFamily(string trigger)
    {
        Assert.Equal(ArbiterTriggerFamily.Investigate, ArbiterTriggerFamilies.ForTrigger(trigger));
    }

    [Theory]
    [InlineData("lens.spawn", ArbiterTriggerFamily.LensSpawn)]
    [InlineData("refute.spawn", ArbiterTriggerFamily.RefuteSpawn)]
    [InlineData("lens.returned", ArbiterTriggerFamily.LensReturned)]
    [InlineData("gate.select", ArbiterTriggerFamily.Gate)]
    public void ForTrigger_ReviewAndGateTriggers_MapToOwnFamily(string trigger, ArbiterTriggerFamily family)
    {
        Assert.Equal(family, ArbiterTriggerFamilies.ForTrigger(trigger));
    }

    [Fact]
    public void UndecidableEffect_ConductorAndInvestigate_MapToEscalate()
    {
        Assert.Equal(RuleEffects.Escalate, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Conductor));
        Assert.Equal(RuleEffects.Escalate, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Investigate));
    }

    [Fact]
    public void UndecidableEffect_ReviewFamilies_MapToAllow()
    {
        Assert.Equal(RuleEffects.Allow, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.LensSpawn));
        Assert.Equal(RuleEffects.Allow, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.RefuteSpawn));
        Assert.Equal(RuleEffects.Allow, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.LensReturned));
    }

    [Fact]
    public void UndecidableEffect_Gate_MapsToApplies()
    {
        Assert.Equal(RuleEffects.Applies, ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamily.Gate));
    }

    [Fact]
    public void Combine_ConductorFamily_EscalateWinsOverAllow()
    {
        Assert.Equal(
            RuleEffects.Escalate,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.Conductor, [RuleEffects.Allow, RuleEffects.Escalate]));
        Assert.Equal(
            RuleEffects.Allow,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.Conductor, [RuleEffects.Allow]));
    }

    [Fact]
    public void Combine_LensSpawn_SkipWinsOverAllow()
    {
        Assert.Equal(
            RuleEffects.Skip,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.LensSpawn, [RuleEffects.Allow, RuleEffects.Skip]));
    }

    [Fact]
    public void Combine_RefuteSpawn_VerifyWinsOverAllow()
    {
        Assert.Equal(
            RuleEffects.Verify,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.RefuteSpawn, [RuleEffects.Allow, RuleEffects.Verify]));
    }

    [Fact]
    public void Combine_LensReturned_AnyAnnotateDeliversAnnotate()
    {
        Assert.Equal(
            RuleEffects.Annotate,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.LensReturned, [RuleEffects.Allow, RuleEffects.Annotate]));
        Assert.Equal(
            RuleEffects.Allow,
            ArbiterTriggerFamilies.Combine(ArbiterTriggerFamily.LensReturned, [RuleEffects.Allow]));
    }

    [Fact]
    public void IsStrongestEffect_EscalateSkipVerify_AreStrongest()
    {
        Assert.True(ArbiterTriggerFamilies.IsStrongestEffect(RuleEffects.Escalate));
        Assert.True(ArbiterTriggerFamilies.IsStrongestEffect(RuleEffects.Skip));
        Assert.True(ArbiterTriggerFamilies.IsStrongestEffect(RuleEffects.Verify));
        Assert.False(ArbiterTriggerFamilies.IsStrongestEffect(RuleEffects.Allow));
        Assert.False(ArbiterTriggerFamilies.IsStrongestEffect(RuleEffects.Annotate));
    }

    [Fact]
    public void EvaluateStep0_AnswersAreNeverAveraged_OneEscalateEscalatesTrigger()
    {
        ArbiterRule allow = DecideRule(
            "KW-ARB-A-001",
            "delegate",
            new RuleDecideClause(RulePredicate.Exists("plan.task", true), "in-task-files"));
        ArbiterRule escalate = DecideRule(
            "KW-ARB-B-001",
            "delegate",
            new RuleDecideClause(RulePredicate.Exists("plan.task", false), "not-in-plan"));
        ArbiterFactSet facts = Facts(("plan.task", "T3", ArbiterFactLabel.Asserted));
        ArbiterOutcome outcome = RuleEngine.EvaluateStep0([allow, escalate], facts, "delegate");
        Assert.Equal(RuleEffects.Escalate, outcome.CombinedEffect);
    }

    // ---- fact labels ----

    [Fact]
    public void FactSet_EveryFact_CarriesDerivedOrAssertedLabel()
    {
        ArbiterFactSet facts = Facts(
            ("plan.task", "T3", ArbiterFactLabel.Asserted),
            ("plan.digest", "abc", ArbiterFactLabel.Derived));
        Assert.True(facts.TryGet("plan.task", out ArbiterFact? asserted) && asserted.Label == ArbiterFactLabel.Asserted);
        Assert.True(facts.TryGet("plan.digest", out ArbiterFact? derived) && derived.Label == ArbiterFactLabel.Derived);
    }
}
