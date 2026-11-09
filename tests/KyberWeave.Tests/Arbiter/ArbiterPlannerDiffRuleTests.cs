using System.Text;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract tests for rule <c>KW-ARB-PLANNER-DIFF-001</c>: a planner return must write only inside
/// the plan, spec and todo directories. Absent changed paths (such as when git cannot observe the
/// repository) answer <c>undecidable</c> (<c>escalate</c>); empty changed paths answer <c>inside</c>
/// (<c>allow</c>); writes exclusively within planning directories answer <c>inside</c> (<c>allow</c>);
/// writes outside planning directories answer <c>outside</c> (<c>escalate</c>).
/// </summary>
public sealed class ArbiterPlannerDiffRuleTests
{
    private const string RuleId = "KW-ARB-PLANNER-DIFF-001";
    private const string SinceDispatchFact = "git.changed-paths.since-dispatch";

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

        Assert.True(
            facts.Facts.TryGetValue(SinceDispatchFact, out ArbiterFact? declaredFact),
            "delegate.planner.returned fact bag must declare git.changed-paths.since-dispatch.");
        Assert.Null(declaredFact!.Value);

        if (includeChangedPaths)
        {
            return facts.With(SinceDispatchFact, changedPaths, ArbiterFactLabel.Derived);
        }

        Dictionary<string, ArbiterFact> dict = facts.Facts
            .Where(pair => !string.Equals(pair.Key, SinceDispatchFact, StringComparison.Ordinal))
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
    public void TheTriggerCatalogDeclaresSinceDispatchChangedPathsForDelegatePlannerReturned()
    {
        Assert.True(
            ArbiterTriggerCatalog.TryGetFacts("delegate.planner.returned", out IReadOnlySet<string>? declared),
            "delegate.planner.returned must be catalogued in ArbiterTriggerCatalog.");
        Assert.Contains(SinceDispatchFact, declared);
        Assert.DoesNotContain("review.changed-paths", declared);

        ArbiterFactSet facts = BuildRealPlannerReturnedFacts(DocsConfig());
        Assert.True(
            facts.Facts.TryGetValue(SinceDispatchFact, out ArbiterFact? fact),
            "delegate.planner.returned fact bag must declare git.changed-paths.since-dispatch.");
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

    [Fact]
    public void HookGitFactsOutsideARepositoryLeavesChangedPathsAbsentAndEscalates()
    {
        using TempDirectory nonRepo = new();
        using TempDirectory home = new();
        HookGitFacts gitFacts = new(nonRepo.Path, home.Path);

        ArbiterEvent postEvent = new()
        {
            Harness = "claude",
            Phase = "post",
            Target = "architect",
            HarnessCaller = "conductor",
            Cwd = nonRepo.Path,
            IsDispatch = true,
            Prompt = PlannerPrompt,
        };
        TriggerClassification postClassification = TriggerClassifier.ClassifySingle(
            postEvent, "architect", PlannerPrompt);
        ArbiterFactSet postFacts = TriggerFactBuilder.Build(postClassification, PlannerPrompt, DocsConfig());
        ArbiterFactSet enrichedPost = gitFacts.Enrich(postFacts, postClassification, postEvent);

        Assert.True(enrichedPost.Facts.TryGetValue(SinceDispatchFact, out ArbiterFact? sinceDispatch));
        Assert.Null(sinceDispatch!.Value);

        ArbiterRule rule = ShippedRule();
        string answer = RuleEngine.Decide(rule, enrichedPost);
        Assert.Equal(RuleEngine.Undecidable, answer);
        Assert.Equal(RuleEffects.Escalate, EffectFor(rule, answer));

        const string lensPrompt = "KYBER-ARBITER: true\nLENS: correctness\n\nApply the lens.";
        ArbiterEvent lensEvent = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            HarnessCaller = "code-reviewer",
            Cwd = nonRepo.Path,
            IsDispatch = true,
            Prompt = lensPrompt,
        };
        TriggerClassification lensClassification = TriggerClassifier.ClassifySingle(
            lensEvent, "review-lens", lensPrompt);
        ArbiterFactSet lensFacts = TriggerFactBuilder.Build(lensClassification, lensPrompt, DocsConfig());
        ArbiterFactSet enrichedLens = gitFacts.Enrich(lensFacts, lensClassification, lensEvent);

        Assert.True(enrichedLens.Facts.TryGetValue("review.changed-paths", out ArbiterFact? reviewChanged));
        Assert.Null(reviewChanged!.Value);
        Assert.True(enrichedLens.Facts.TryGetValue("review.base-ref", out ArbiterFact? baseRef));
        Assert.Null(baseRef!.Value);
    }

    [Fact]
    public void HookGitFactsInsideARepositoryMeasuresPlannerDispatchDeltaNotWholeBranchDiff()
    {
        using RunStartProbe probe = RunStartProbe.Create();
        using TempDirectory home = new();
        probe.Git("switch", "-c", "claude/feat/planner-delta", "origin/main");

        // Commit an earlier implementation change on the branch and leave artifacts/ dirty.
        WriteRepoFile(probe.Root, "src/EarlierTask.cs", "// committed by an earlier task\n");
        probe.Git("add", "-A");
        probe.Git("commit", "-m", "feat: earlier task implementation");
        WriteRepoFile(probe.Root, "artifacts/gates.json", "{}\n");

        HookGitFacts gitFacts = new(probe.Root, home.Path);
        ArbiterRule rule = ShippedRule();

        // 1. Unpaired PostToolUse with no planner edits: earlier branch commits and artifacts/ do not contaminate.
        ArbiterFactSet emptyUnpaired = EnrichPlannerReturn(gitFacts, probe.Root, toolCallId: null);
        IReadOnlyList<string> emptyPaths = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            emptyUnpaired.Facts[SinceDispatchFact].Value);
        Assert.Empty(emptyPaths);
        Assert.Equal("inside", RuleEngine.Decide(rule, emptyUnpaired));

        // 2. Pre-existing dirty file before PreToolUse + planner writes only docs/plans/plan.md during dispatch.
        WriteRepoFile(probe.Root, "src/PreExistingDirty.cs", "// dirty before planner dispatch\n");
        EnrichPlannerPre(gitFacts, probe.Root, toolCallId: "call-plan-1");
        WriteRepoFile(probe.Root, "docs/plans/2026-11-09-feature-plan.md", "# Plan\n");

        ArbiterFactSet planOnlyReturn = EnrichPlannerReturn(gitFacts, probe.Root, toolCallId: "call-plan-1");
        IReadOnlyList<string> planOnlyPaths = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            planOnlyReturn.Facts[SinceDispatchFact].Value);
        Assert.Equal(["docs/plans/2026-11-09-feature-plan.md"], planOnlyPaths);
        Assert.Equal("inside", RuleEngine.Decide(rule, planOnlyReturn));

        // 3. Planner writes outside planning directories during dispatch: escalates as outside.
        EnrichPlannerPre(gitFacts, probe.Root, toolCallId: "call-plan-2");
        WriteRepoFile(probe.Root, "src/Rogue.cs", "// rogue planner edit\n");

        ArbiterFactSet rogueReturn = EnrichPlannerReturn(gitFacts, probe.Root, toolCallId: "call-plan-2");
        IReadOnlyList<string> roguePaths = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            rogueReturn.Facts[SinceDispatchFact].Value);
        Assert.Contains("src/Rogue.cs", roguePaths);
        Assert.Equal("outside", RuleEngine.Decide(rule, rogueReturn));
        Assert.Equal(RuleEffects.Escalate, EffectFor(rule, "outside"));
    }

    private static void EnrichPlannerPre(HookGitFacts gitFacts, string repoRoot, string? toolCallId)
    {
        ArbiterEvent preEvent = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "architect",
            HarnessCaller = "conductor",
            Cwd = repoRoot,
            ToolCallId = toolCallId,
            IsDispatch = true,
            Prompt = PlannerPrompt,
        };
        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            preEvent, "architect", PlannerPrompt);
        Assert.Equal("delegate.planner", classification.Trigger);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, PlannerPrompt, DocsConfig());
        _ = gitFacts.Enrich(facts, classification, preEvent);
    }

    private static ArbiterFactSet EnrichPlannerReturn(HookGitFacts gitFacts, string repoRoot, string? toolCallId)
    {
        ArbiterEvent postEvent = new()
        {
            Harness = "claude",
            Phase = "post",
            Target = "architect",
            HarnessCaller = "conductor",
            Cwd = repoRoot,
            ToolCallId = toolCallId,
            IsDispatch = true,
            Prompt = PlannerPrompt,
        };
        TriggerClassification classification = TriggerClassifier.ClassifySingle(
            postEvent, "architect", PlannerPrompt);
        Assert.Equal("delegate.planner.returned", classification.Trigger);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, PlannerPrompt, DocsConfig());
        return gitFacts.Enrich(facts, classification, postEvent);
    }

    private static void WriteRepoFile(string repoRoot, string relativePath, string content)
    {
        string fullPath = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Encoding.UTF8);
    }
}
