using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// One hook invocation resolves one run context, and a Copilot CLI turn can dispatch several workers in one
/// <c>toolCalls</c> batch. Each call must still be judged in the worktree that holds its own plan: reading T2 against
/// T1's worktree means its task file and git facts are somebody else's.
/// </summary>
public sealed class ArbiterBatchEvaluationRootTests
{
    private const string Repo = "/kw/repo";
    private const string WorktreeA = "/kw/run-a";
    private const string WorktreeB = "/kw/run-b";
    private const string PlanA = "docs/plans/a.md";
    private const string PlanB = "docs/plans/b.md";

    /// <summary>T1's plan holds the invocation's run context; T2's plan lives in the other worktree of the run.</summary>
    [Fact]
    public void ASiblingDispatchIsJudgedInTheWorktreeThatHoldsItsOwnPlan()
    {
        HookContext context = Context(worktree: WorktreeA, planFile: PlanA);
        PlanRoots roots = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PlanA] = WorktreeA,
            [PlanB] = WorktreeB,
        });

        Assert.Equal(
            WorktreeB,
            ArbiterHookDecisionEngine.EvaluationRootFor(
                context, Repo, $"KYBER-ARBITER: true\nPLAN_FILE: {PlanB}\nTASK: T2\n\nTask T2.", roots));
    }

    /// <summary>The dispatch the context was resolved from is judged exactly where the context says.</summary>
    [Fact]
    public void TheDispatchTheContextWasResolvedFromKeepsTheContextsWorktree()
    {
        HookContext context = Context(worktree: WorktreeA, planFile: PlanA);
        PlanRoots roots = new(new Dictionary<string, string>(StringComparer.Ordinal) { [PlanA] = WorktreeB });

        Assert.Equal(
            WorktreeA,
            ArbiterHookDecisionEngine.EvaluationRootFor(
                context, Repo, $"KYBER-ARBITER: true\nPLAN_FILE: {PlanA}\nTASK: T1\n\nTask T1.", roots));
    }

    /// <summary>A plan that resolves nowhere is read where the invocation's evidence put the work, as before.</summary>
    [Fact]
    public void APlanTheRepositoryDoesNotHoldKeepsTheContextsWorktree()
    {
        HookContext context = Context(worktree: WorktreeA, planFile: PlanA);
        PlanRoots roots = new(new Dictionary<string, string>(StringComparer.Ordinal) { [PlanA] = WorktreeA });

        Assert.Equal(
            WorktreeA,
            ArbiterHookDecisionEngine.EvaluationRootFor(
                context, Repo, $"KYBER-ARBITER: true\nPLAN_FILE: docs/plans/gone.md\nTASK: T2\n\nTask T2.", roots));
    }

    /// <summary>
    /// A sibling whose plan lives only in the repository's checkout is judged there. The checkout is a worktree of
    /// this repository like any other, and judging the call in the run's worktree instead reads a plan that is not
    /// there — so its task file, its readiness facts and its git facts are all somebody else's absence.
    /// </summary>
    [Fact]
    public void ASiblingDispatchWhosePlanOnlyTheCheckoutHoldsIsJudgedInTheCheckout()
    {
        const string planC = "docs/plans/c.md";
        HookContext context = Context(worktree: WorktreeA, planFile: PlanA);
        PlanRoots roots = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PlanA] = WorktreeA,
            [planC] = Repo,
        });

        Assert.Equal(
            Repo,
            ArbiterHookDecisionEngine.EvaluationRootFor(
                context, Repo, $"KYBER-ARBITER: true\nPLAN_FILE: {planC}\nTASK: T3\n\nTask T3.", roots));
    }

    /// <summary>
    /// Several live runs and no signal: the context is ambiguous, reads paths from the canonical checkout and names no
    /// plan. A dispatch that names its plan is still judged in the worktree that holds it.
    /// </summary>
    [Fact]
    public void AnAmbiguousContextJudgesADispatchThatNamesItsPlanWhereThePlanIs()
    {
        PlanRoots roots = new(new Dictionary<string, string>(StringComparer.Ordinal) { [PlanB] = WorktreeB });

        Assert.Equal(
            WorktreeB,
            ArbiterHookDecisionEngine.EvaluationRootFor(
                AmbiguousContext(), Repo, $"KYBER-ARBITER: true\nPLAN_FILE: {PlanB}\nTASK: T2\n\nTask T2.", roots));
    }

    /// <summary>Without a plan, an ambiguous context is judged in the canonical checkout, never in a guessed run.</summary>
    [Fact]
    public void AnAmbiguousContextJudgesADispatchWithoutAPlanInTheCanonicalCheckout()
    {
        PlanRoots roots = new(new Dictionary<string, string>(StringComparer.Ordinal) { [PlanB] = WorktreeB });

        Assert.Equal(
            Repo,
            ArbiterHookDecisionEngine.EvaluationRootFor(AmbiguousContext(), Repo, "KYBER-ARBITER: true\nTASK: T2\n\nTask T2.", roots));
    }

    private static HookContext AmbiguousContext() =>
        new(
            Repo,
            () => "decision-test",
            TextWriter.Null,
            RunContext: new HookRunContext(
                RepositoryRoot: Repo,
                CommonDirectory: "/kw/repo/.git",
                ActiveRun: null,
                Worktree: Repo,
                PlanFile: null,
                PlanPath: null,
                TaskFile: null,
                TaskFileExists: false,
                Worktrees: [Repo, WorktreeA, WorktreeB],
                Source: HookRunContextSource.Ambiguous,
                Notes: ["several runs are live: '/kw/run-a', '/kw/run-b'."]));

    private static HookContext Context(string worktree, string planFile) =>
        new(
            Repo,
            () => "decision-test",
            TextWriter.Null,
            RunContext: new HookRunContext(
                RepositoryRoot: Repo,
                CommonDirectory: "/kw/repo/.git",
                ActiveRun: null,
                Worktree: worktree,
                PlanFile: planFile,
                PlanPath: $"{worktree}/{planFile}",
                TaskFile: null,
                TaskFileExists: false,
                Worktrees: [Repo, WorktreeA, WorktreeB],
                Source: HookRunContextSource.ActiveRun,
                Notes: []));

    /// <summary>Answers the plan-holding worktree from a table, and the repository root when the table names none.</summary>
    private sealed class PlanRoots(Dictionary<string, string> byPlan) : IPlanRootResolver
    {
        public string Resolve(string hookRoot, string prompt, Func<string, IReadOnlyList<string>>? listWorktrees = null) =>
            ResolveByName(hookRoot, PlanIn(prompt), listWorktrees);

        public string ResolvePlanRoot(
            string hookRoot,
            string? planFile,
            string? text,
            Func<string, IReadOnlyList<string>>? listWorktrees = null) =>
            ResolveByName(hookRoot, planFile, listWorktrees);

        private string ResolveByName(
            string hookRoot, string? planFile, Func<string, IReadOnlyList<string>>? listWorktrees) =>
            planFile is not null && byPlan.TryGetValue(planFile, out string? root) ? root : hookRoot;

        private static string? PlanIn(string prompt) =>
            prompt.Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("PLAN_FILE:", StringComparison.Ordinal))?["PLAN_FILE:".Length..].Trim();
    }
}
