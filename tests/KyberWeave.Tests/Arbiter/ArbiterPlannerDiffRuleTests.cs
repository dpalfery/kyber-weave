using System;
using System.Collections.Generic;
using System.Linq;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract tests for rule <c>KW-ARB-PLANNER-DIFF-001</c>: a planner return must write only inside
/// the plan, spec and todo directories. Absent changed paths (such as when git facts are uncomputed or clean)
/// and empty changed paths answer <c>inside</c> (<c>allow</c>); writes exclusively within planning directories
/// answer <c>inside</c> (<c>allow</c>); writes outside planning directories answer <c>outside</c> (<c>escalate</c>).
/// </summary>
public sealed class ArbiterPlannerDiffRuleTests
{
    private const string RuleId = "KW-ARB-PLANNER-DIFF-001";

    private static readonly string[] DefaultPlanningDirs =
    [
        "docs/plans/**",
        "docs/specs/**",
        "docs/todo/**",
    ];

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

    private static ArbiterFactSet Facts(object? changedPaths = null, bool includeChangedPaths = true)
    {
        ArbiterFactSet facts = new();
        facts = facts.With("config.planning-dirs", DefaultPlanningDirs, ArbiterFactLabel.Derived);
        if (includeChangedPaths)
        {
            facts = facts.With("git.changed-paths.since-dispatch", changedPaths, ArbiterFactLabel.Derived);
        }

        return facts;
    }

    private static ArbiterFactSet PlannerReturnedFacts(object? changedPaths = null, bool includeChangedPaths = true)
    {
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            "delegate.planner.returned",
            "architect",
            "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nPlan prompt",
            "conductor",
            "harness",
            DocsConfig());

        if (includeChangedPaths)
        {
            return facts.With("git.changed-paths.since-dispatch", changedPaths, ArbiterFactLabel.Derived);
        }

        Dictionary<string, ArbiterFact> dict = facts.Facts
            .Where(pair => !string.Equals(pair.Key, "git.changed-paths.since-dispatch", StringComparison.Ordinal))
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
    public void AbsentChangedPathsFactAnswersInsideAndAllows()
    {
        ArbiterRule rule = ShippedRule();
        ArbiterFactSet facts = Facts(includeChangedPaths: false);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("inside", answer);
        Assert.Equal(RuleEffects.Allow, effect);
    }

    [Fact]
    public void NullValuedChangedPathsFactAnswersInsideAndAllows()
    {
        ArbiterRule rule = ShippedRule();
        ArbiterFactSet facts = PlannerReturnedFacts(changedPaths: null, includeChangedPaths: true);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("inside", answer);
        Assert.Equal(RuleEffects.Allow, effect);
    }

    [Fact]
    public void EmptyChangedPathsAnswersInsideAndAllows()
    {
        ArbiterRule rule = ShippedRule();
        ArbiterFactSet facts = Facts(Array.Empty<string>());

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
        ArbiterFactSet facts = Facts(paths);

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
        ArbiterFactSet facts = Facts(paths);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("inside", answer);
        Assert.Equal(RuleEffects.Allow, effect);
    }

    [Theory]
    [InlineData("src/KyberWeave.Core/Arbiter/Rules/ArbiterRule.cs")]
    [InlineData("docs/adr/0001-install.md")]
    [InlineData("README.md")]
    public void ChangedPathsOutsidePlanningDirectoriesAnswersOutsideAndEscalates(string path)
    {
        ArbiterRule rule = ShippedRule();
        string[] paths = [path];
        ArbiterFactSet facts = Facts(paths);

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
        ArbiterFactSet facts = Facts(paths);

        string answer = RuleEngine.Decide(rule, facts);
        string effect = EffectFor(rule, answer);

        Assert.Equal("outside", answer);
        Assert.Equal(RuleEffects.Escalate, effect);
    }
}
