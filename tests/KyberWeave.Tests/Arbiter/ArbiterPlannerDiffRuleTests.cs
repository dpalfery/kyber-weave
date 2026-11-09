using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract tests for rule <c>KW-ARB-PLANNER-DIFF-001</c>: a planner return must write only inside
/// the plan, spec and todo directories. Absent changed paths (such as when git facts are uncomputed)
/// answer <c>undecidable</c> (<c>escalate</c>); empty changed paths answer <c>inside</c> (<c>allow</c>);
/// writes exclusively within planning directories answer <c>inside</c> (<c>allow</c>); writes outside
/// planning directories answer <c>outside</c> (<c>escalate</c>).
/// </summary>
public sealed class ArbiterPlannerDiffRuleTests
{
    private const string RuleId = "KW-ARB-PLANNER-DIFF-001";

    private const string PlannerPrompt =
        "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nPlan prompt";

    private static KyberWeaveConfig DocsConfig() => new()
    {
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private static ArbiterRule ShippedRule()
    {
        return Assert.Single(
            ArbiterConfig.ProductDefaults.Rules,
            rule => string.Equals(rule.Id, RuleId, StringComparison.Ordinal));
    }

    private static ArbiterFactSet BuildRealPlannerReturnedFacts(KyberWeaveConfig config)
    {
        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            new ArbiterEvent
            {
                Harness = "claude",
                Phase = "post",
                Target = "architect",
                HarnessCaller = "conductor",
                IsDispatch = true,
                Prompt = PlannerPrompt,
            },
            "architect",
            PlannerPrompt);

        Assert.Equal("delegate.planner.returned", classification.Trigger);
        return TriggerFactBuilder.Build(classification, PlannerPrompt, config);
    }

    private static ArbiterFactSet PlannerReturnedFacts(
        IReadOnlyList<string>? changedPaths = null,
        bool includeChangedPaths = true)
    {
        ArbiterFactSet facts = BuildRealPlannerReturnedFacts(DocsConfig());

        if (facts.Facts.TryGetValue("review.changed-paths", out ArbiterFact? declaredFact))
        {
            Assert.Null(declaredFact.Value);
        }

        if (includeChangedPaths)
        {
            return facts.With("review.changed-paths", changedPaths, ArbiterFactLabel.Derived);
        }

        Dictionary<string, ArbiterFact> dict = facts.Facts
            .Where(pair => !string.Equals(pair.Key, "review.changed-paths", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new ArbiterFactSet(dict);
    }

    private static string EffectFor(ArbiterRule rule, string answer)
    {
        return rule.Effects.TryGetValue(answer, out string? effect)
            ? effect
            : ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamilies.ForTrigger(rule.Trigger));
    }

    [Fact]
    public void TheShippedRuleRunsOnTheDelegatePlannerReturnedTrigger()
    {
        Assert.Equal("delegate.planner.returned", ShippedRule().Trigger);
    }

    [Fact]
    public void TheTriggerCatalogDeclaresReviewChangedPathsForDelegatePlannerReturned()
    {
        Assert.True(
            ArbiterTriggerCatalog.TryGetFacts("delegate.planner.returned", out IReadOnlySet<string>? declared),
            "delegate.planner.returned must be catalogued in ArbiterTriggerCatalog.");
        Assert.Contains("review.changed-paths", declared);

        ArbiterFactSet facts = BuildRealPlannerReturnedFacts(DocsConfig());
        Assert.True(
            facts.Facts.TryGetValue("review.changed-paths", out ArbiterFact? fact),
            "delegate.planner.returned fact bag must declare review.changed-paths.");
        Assert.Null(fact!.Value);
    }

    [Fact]
    public void FactBagCarriesPlanningDirsDerivedFromConfigRegistry()
    {
        ArbiterFactSet facts = BuildRealPlannerReturnedFacts(DocsConfig());

        IReadOnlyList<string> dirs = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            facts.Facts["config.planning-dirs"].Value);
        Assert.Equal(["docs/plans/**", "docs/specs/**", "docs/todo/**"], dirs);
        Assert.Equal(ArbiterFactLabel.Derived, facts.Facts["config.planning-dirs"].Label);
    }

    [Fact]
    public void AbsentChangedPathsFactAnswersUndecidableAndEscalates()
    {
        ArbiterRule rule = ShippedRule();
        ArbiterFactSet facts = PlannerReturnedFacts(includeChangedPaths: false);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal(RuleEngine.Undecidable, answer);
        Assert.Equal(RuleEffects.Escalate, effect);
    }

    [Fact]
    public void NullValuedChangedPathsFactAnswersUndecidableAndEscalates()
    {
        ArbiterRule rule = ShippedRule();
        ArbiterFactSet facts = PlannerReturnedFacts(changedPaths: null, includeChangedPaths: true);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal(RuleEngine.Undecidable, answer);
        Assert.Equal(RuleEffects.Escalate, effect);
    }

    [Fact]
    public void EmptyChangedPathsAnswersInsideAndAllows()
    {
        ArbiterRule rule = ShippedRule();
        ArbiterFactSet facts = PlannerReturnedFacts(Array.Empty<string>());

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("inside", answer);
        Assert.Equal(RuleEffects.Allow, effect);
    }

    [Theory]
    [InlineData("docs/plans/2026-11-09-feature-plan.md")]
    [InlineData("docs/specs/planner/requirements.md")]
    [InlineData("docs/todo/deferred-task.md")]
    public void ChangedPathsInsidePlanningDirectoriesAnswersInsideAndAllows(string path)
    {
        ArbiterRule rule = ShippedRule();
        string[] paths = [path];
        ArbiterFactSet facts = PlannerReturnedFacts(paths);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("inside", answer);
        Assert.Equal(RuleEffects.Allow, effect);
    }

    [Fact]
    public void MultipleChangedPathsInsidePlanningDirectoriesAnswersInsideAndAllows()
    {
        ArbiterRule rule = ShippedRule();
        string[] paths =
        [
            "docs/plans/2026-11-09-feature-plan.md",
            "docs/specs/planner/requirements.md",
            "docs/todo/deferred-task.md",
        ];
        ArbiterFactSet facts = PlannerReturnedFacts(paths);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("inside", answer);
        Assert.Equal(RuleEffects.Allow, effect);
    }

    [Theory]
    [InlineData("src/KyberWeave.Core/Arbiter/Rules/ArbiterRule.cs")]
    [InlineData("docs/adr/0001-install.md")]
    [InlineData("README.md")]
    [InlineData("src/payload.cs")]
    public void ChangedPathsOutsidePlanningDirectoriesAnswersOutsideAndEscalates(string path)
    {
        ArbiterRule rule = ShippedRule();
        string[] paths = [path];
        ArbiterFactSet facts = PlannerReturnedFacts(paths);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("outside", answer);
        Assert.Equal(RuleEffects.Escalate, effect);
    }

    [Fact]
    public void MixedChangedPathsOutsidePlanningDirectoriesAnswersOutsideAndEscalates()
    {
        ArbiterRule rule = ShippedRule();
        string[] paths =
        [
            "docs/plans/2026-11-09-feature-plan.md",
            "src/KyberWeave.Core/Arbiter/Rules/ArbiterRule.cs",
        ];
        ArbiterFactSet facts = PlannerReturnedFacts(paths);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("outside", answer);
        Assert.Equal(RuleEffects.Escalate, effect);
    }
}
