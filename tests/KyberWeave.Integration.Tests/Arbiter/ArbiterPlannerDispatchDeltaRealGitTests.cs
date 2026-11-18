using System.Text;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Arbiter;
using Xunit;

namespace KyberWeave.Integration.Tests.Arbiter;

/// <summary>
/// The planner-return diff rule <c>KW-ARB-PLANNER-DIFF-001</c> measured over a real git repository: the paths a
/// planner dispatch changed are the delta since its pre-dispatch snapshot, not the whole branch diff. Only real git can
/// show a committed earlier change and a dirty tree apart from the dispatch's own writes.
/// </summary>
public sealed class ArbiterPlannerDispatchDeltaRealGitTests
{
    private const string RuleId = "KW-ARB-PLANNER-DIFF-001";
    private const string SinceDispatchFact = "git.changed-paths.since-dispatch";
    private const string AttributedFact = "git.changed-paths.attributed";

    private const string PlannerPrompt =
        "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nPlan prompt";

    private static KyberWeaveConfig DocsConfig() => new()
    {
        Arbiter = ArbiterConfigDefaults.Product,
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private static ArbiterRule ShippedRule()
    {
        return Assert.Single(
            ArbiterConfigDefaults.Product.Rules,
            rule => string.Equals(rule.Id, RuleId, StringComparison.Ordinal));
    }

    private static string EffectFor(ArbiterRule rule, string answer)
    {
        return rule.Effects.TryGetValue(answer, out string? effect)
            ? effect
            : ArbiterTriggerFamilies.UndecidableEffect(ArbiterTriggerFamilies.ForTrigger(rule.Trigger));
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

        HookGitFacts gitFacts = ArbiterTestServices.HookGitFacts(probe.Root, home.Path);
        ArbiterRule rule = ShippedRule();

        // 1. Unpaired PostToolUse with no pre-dispatch snapshot: the dispatch delta is
        // unobservable, so the fact stays absent. Falling back to a diff against HEAD would
        // read a committed dispatch write as an empty diff (inside/allow) and leave the
        // outside branch dead, so the rule stays undecidable and allows (ADR 0072).
        ArbiterFactSet unpairedReturn = EnrichPlannerReturn(gitFacts, probe.Root, toolCallId: null);
        Assert.True(unpairedReturn.Facts.TryGetValue(SinceDispatchFact, out ArbiterFact? unpairedFact));
        Assert.Null(unpairedFact!.Value);
        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(rule, unpairedReturn));
        Assert.Equal(RuleEffects.Allow, EffectFor(rule, RuleEngine.Undecidable));

        // 2. Pre-existing dirty file before PreToolUse + planner writes only docs/plans/plan.md during dispatch.
        WriteRepoFile(probe.Root, "src/PreExistingDirty.cs", "// dirty before planner dispatch\n");
        EnrichPlannerPre(gitFacts, probe.Root, toolCallId: "call-plan-1");
        WriteRepoFile(probe.Root, "docs/plans/2026-11-09-feature-plan.md", "# Plan\n");

        ArbiterFactSet planOnlyReturn = AttributedToThePlanner(
            EnrichPlannerReturn(gitFacts, probe.Root, toolCallId: "call-plan-1"));
        IReadOnlyList<string> planOnlyPaths = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            planOnlyReturn.Facts[SinceDispatchFact].Value);
        Assert.Equal(["docs/plans/2026-11-09-feature-plan.md"], planOnlyPaths);
        Assert.Equal("inside", RuleEngine.Decide(rule, planOnlyReturn));

        // 3. Planner writes outside planning directories during dispatch: escalates as outside.
        EnrichPlannerPre(gitFacts, probe.Root, toolCallId: "call-plan-2");
        WriteRepoFile(probe.Root, "src/Rogue.cs", "// rogue planner edit\n");

        ArbiterFactSet rogueReturn = AttributedToThePlanner(
            EnrichPlannerReturn(gitFacts, probe.Root, toolCallId: "call-plan-2"));
        IReadOnlyList<string> roguePaths = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            rogueReturn.Facts[SinceDispatchFact].Value);
        Assert.Contains("src/Rogue.cs", roguePaths);
        Assert.Equal("outside", RuleEngine.Decide(rule, rogueReturn));
        Assert.Equal(RuleEffects.Escalate, EffectFor(rule, "outside"));
    }

    /// <summary>With no dispatch in flight beside it, the planner owns every path that changed.</summary>
    private static ArbiterFactSet AttributedToThePlanner(ArbiterFactSet facts) =>
        facts.With(AttributedFact, facts.Facts[SinceDispatchFact].Value, ArbiterFactLabel.Derived);

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
