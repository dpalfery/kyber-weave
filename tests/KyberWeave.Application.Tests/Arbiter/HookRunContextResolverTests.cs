using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The one run-context resolver: its precedence, its containment checks, and the absence notes a guard escalates with.
/// </summary>
public sealed class HookRunContextResolverTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeGitRepositoryFacts _git = new();
    private readonly FakeProcessEnvironment _environment;
    private readonly FakeRunStateStore _runs = new();
    private readonly FakeCheckedOutBranchReader _branches = new();
    private readonly FakePathResolver _paths;
    private readonly PlanRootResolver _planRoots;
    private readonly HookRunContextResolver _resolver;

    private readonly string _main;
    private readonly string _runWorktree;
    private readonly string _otherWorktree;
    private readonly string _taskWorktree;

    public HookRunContextResolverTests()
    {
        _main = ArbiterTestRoots.Under("kw", "repo");
        _runWorktree = ArbiterTestRoots.Under("kw", "run-worktree");
        _otherWorktree = ArbiterTestRoots.Under("kw", "other-worktree");
        _taskWorktree = ArbiterTestRoots.Under("kw", "task-worktree");
        _git.CommonDirectory = Path.Combine(_main, ".git");
        foreach (string directory in new[] { _main, _runWorktree, _otherWorktree, _taskWorktree })
        {
            _files.CreateDirectory(directory);
            _git.Worktrees[directory] = [_main, _runWorktree, _otherWorktree, _taskWorktree];
        }

        _paths = new FakePathResolver(_files);
        _environment = new FakeProcessEnvironment(currentDirectory: _main, homeDirectory: ArbiterTestRoots.Under("home"));
        _planRoots = new PlanRootResolver(_git, new ContainedPlanFileResolver(), _files, _paths);
        _resolver = new HookRunContextResolver(
            _git,
            new ContainedPlanFileResolver(),
            _planRoots,
            _files,
            _paths,
            _environment,
            _runs,
            _branches);
    }

    [Fact]
    public void ThePayloadCwdIsTheCanonicalCheckoutWhileThePlanIsInTheRunWorktree_ResolvesTheRunWorktree()
    {
        // Run 9: the Copilot session started in the original checkout, and `run start` put the plan in a worktree.
        WritePlan(_runWorktree, "docs/plans/2026-11-18-run.md");

        HookRunContext context = Resolve("PLAN_FILE: docs/plans/2026-11-18-run.md\nTASK: T1");

        Assert.Equal(HookRunContextSource.EventWorktree, context.Source);
        Assert.Equal(_runWorktree, context.Worktree);
        Assert.Equal(Path.Combine(_runWorktree, "docs", "plans", "2026-11-18-run.md"), context.PlanPath);
        Assert.True(context.HasTaskFile);
        Assert.Equal(
            Path.Combine(_runWorktree, "docs", "plans", "2026-11-18-run.tasks.yml"),
            context.TaskFile);
    }

    [Fact]
    public void TheLiveRunRecordChoosesTheWorktreeWhenTheDispatchNamesNoPlan()
    {
        _runs.Records = [new RunRecord(_git.CommonDirectory!, "opencode/fix/run", _runWorktree, DateTimeOffset.UtcNow)];
        _branches.Branches[_runWorktree] = "opencode/fix/run";

        HookRunContext context = Resolve("TASK: T1");

        Assert.Equal(HookRunContextSource.ActiveRun, context.Source);
        Assert.Equal(_runWorktree, context.Worktree);
        Assert.Equal("opencode/fix/run", context.ActiveRun?.Branch);
    }

    [Fact]
    public void ARunRecordWhoseWorktreeHasAnotherBranchCheckedOutIsNotUsed()
    {
        _runs.Records = [new RunRecord(_git.CommonDirectory!, "opencode/fix/run", _runWorktree, DateTimeOffset.UtcNow)];
        _branches.Branches[_runWorktree] = "opencode/other/run";

        HookRunContext context = Resolve("TASK: T1");

        Assert.Null(context.ActiveRun);
        Assert.Equal(_main, context.Worktree);
        Assert.Contains(context.Notes, note => note.Contains("branch", StringComparison.Ordinal));
    }

    /// <summary>
    /// Defect 9: with two live runs and no signal naming either, the newer one used to win, so a worker of the older
    /// run was judged by the newer run's plan and files. Now nothing is chosen, and the note names both runs.
    /// </summary>
    [Fact]
    public void TwoLiveRunsAndNoSignalAreAmbiguousAndTheNoteNamesBoth()
    {
        TwoLiveRuns();

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", cwd: null));

        Assert.Equal(HookRunContextSource.Ambiguous, context.Source);
        Assert.Null(context.ActiveRun);
        Assert.Equal(_main, context.Worktree);
        Assert.Contains(
            context.Notes,
            note => note.Contains(_runWorktree, StringComparison.Ordinal)
                && note.Contains(_otherWorktree, StringComparison.Ordinal));
    }

    /// <summary>The Read guard's roots: an ambiguous context still lists every worktree of the repository.</summary>
    [Fact]
    public void AnAmbiguousContextStillListsEveryWorktree()
    {
        TwoLiveRuns();

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", cwd: null));

        Assert.Equal(HookRunContextSource.Ambiguous, context.Source);
        Assert.Equal(
            new[] { _main, _runWorktree, _otherWorktree, _taskWorktree }.Order(StringComparer.Ordinal),
            context.Worktrees.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ACwdInsideTheOlderRunsWorktreeResolvesThatRun()
    {
        TwoLiveRuns();
        string cwd = Path.Combine(_runWorktree, "src");
        _files.CreateDirectory(cwd);
        _git.Worktrees[cwd] = _git.Worktrees[_main];

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", cwd: cwd));

        Assert.Equal(_runWorktree, context.Worktree);
        Assert.Equal("run/a", context.ActiveRun?.Branch);
        Assert.Equal(_runWorktree, context.ActiveRun?.Worktree);
    }

    [Fact]
    public void ACwdInATaskWorktreeWhoseBranchDescendsFromOneRunResolvesThatTaskWorktreeAndRun()
    {
        TwoLiveRuns();
        _branches.Branches[_taskWorktree] = "task/t1";
        _branches.Ancestry[("run/a", "task/t1")] = true;
        _branches.Ancestry[("run/b", "task/t1")] = false;

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", cwd: _taskWorktree));

        Assert.Equal(_taskWorktree, context.Worktree);
        Assert.Equal("run/a", context.ActiveRun?.Branch);
        Assert.NotEqual(HookRunContextSource.Ambiguous, context.Source);
    }

    [Fact]
    public void AnUnscriptedAncestryPairAnswersNullAndLeavesTheRunContextAmbiguous()
    {
        TwoLiveRuns();
        _branches.Branches[_taskWorktree] = "task/t1";
        _branches.Ancestry[("run/a", "task/t1")] = true;

        Assert.Null(_branches.IsAncestor(_taskWorktree, "run/b", "task/t1"));

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", cwd: _taskWorktree));

        Assert.Equal(HookRunContextSource.Ambiguous, context.Source);
        Assert.Null(context.ActiveRun);
        Assert.Equal(_main, context.Worktree);
        Assert.Contains(
            context.Notes,
            note => note.Contains("git could not tell whether 'run/b' is an ancestor of 'task/t1'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACwdInATaskWorktreeWhoseBranchDescendsFromBothRunsOrNeitherIsAmbiguous(bool descendsFromBoth)
    {
        TwoLiveRuns();
        _branches.Branches[_taskWorktree] = "task/t1";
        _branches.Ancestry[("run/a", "task/t1")] = descendsFromBoth;
        _branches.Ancestry[("run/b", "task/t1")] = descendsFromBoth;

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", cwd: _taskWorktree));

        Assert.Equal(HookRunContextSource.Ambiguous, context.Source);
        Assert.Null(context.ActiveRun);
        Assert.Equal(_main, context.Worktree);
        Assert.Contains(_taskWorktree, context.Worktrees);
    }

    [Fact]
    public void ALeadingCdIntoTheOlderRunFromTheCanonicalCheckoutResolvesThatRun()
    {
        TwoLiveRuns();

        HookRunContext context = _resolver.Resolve(
            Request("TASK: T1", cwd: _main, command: $"cd {_runWorktree} && dotnet test"));

        Assert.Equal(_runWorktree, context.Worktree);
        Assert.Equal("run/a", context.ActiveRun?.Branch);
    }

    [Fact]
    public void ALeadingCdIntoAnotherRunIsIgnoredWithANoteWhenTheCwdIsInsideARun()
    {
        TwoLiveRuns();

        HookRunContext context = _resolver.Resolve(
            Request("TASK: T1", cwd: _runWorktree, command: $"cd {_otherWorktree} && git status"));

        Assert.Equal(_runWorktree, context.Worktree);
        Assert.Equal("run/a", context.ActiveRun?.Branch);
        Assert.Contains(
            context.Notes,
            note => note.Contains(_otherWorktree, StringComparison.Ordinal)
                && note.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void AGitDirectoryOptionIsNoSignal()
    {
        TwoLiveRuns();

        HookRunContext context = _resolver.Resolve(
            Request("TASK: T1", cwd: _runWorktree, command: $"git -C {_otherWorktree} status"));

        Assert.Equal(_runWorktree, context.Worktree);
        Assert.Equal("run/a", context.ActiveRun?.Branch);
    }

    [Fact]
    public void AGitDirectoryOptionFromTheCanonicalCheckoutChoosesNoRun()
    {
        TwoLiveRuns();

        HookRunContext context = _resolver.Resolve(
            Request("TASK: T1", cwd: _main, command: $"git -C {_runWorktree} status"));

        Assert.Equal(HookRunContextSource.Ambiguous, context.Source);
        Assert.Null(context.ActiveRun);
    }

    [Fact]
    public void OneLiveRunAndNoSignalIsThatRunAsBefore()
    {
        _runs.Records = [new RunRecord(_git.CommonDirectory!, "run/a", _runWorktree, DateTimeOffset.UtcNow)];
        _branches.Branches[_runWorktree] = "run/a";

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", cwd: null));

        Assert.Equal(HookRunContextSource.ActiveRun, context.Source);
        Assert.Equal(_runWorktree, context.Worktree);
        Assert.Equal("run/a", context.ActiveRun?.Branch);
    }

    [Fact]
    public void TheAgentWorktreeOutranksTheCwdAndALeadingCd()
    {
        TwoLiveRuns();

        HookRunContext context = _resolver.Resolve(
            Request("TASK: T1", agentWorktree: _otherWorktree, cwd: _main, command: $"cd {_runWorktree} && ls"));

        Assert.Equal(HookRunContextSource.EventWorktree, context.Source);
        Assert.Equal(_otherWorktree, context.Worktree);
        Assert.Equal("run/b", context.ActiveRun?.Branch);
    }

    [Fact]
    public void ThePlanWorktreeOutranksTheCwd()
    {
        TwoLiveRuns();
        WritePlan(_otherWorktree, "docs/plans/2026-11-24-b.md");

        HookRunContext context = _resolver.Resolve(
            Request("PLAN_FILE: docs/plans/2026-11-24-b.md\nTASK: T1", cwd: _runWorktree));

        Assert.Equal(HookRunContextSource.EventWorktree, context.Source);
        Assert.Equal(_otherWorktree, context.Worktree);
        Assert.Equal("run/b", context.ActiveRun?.Branch);
    }

    [Fact]
    public void AWorktreeTheEventNamesWinsOverTheActiveRun()
    {
        _runs.Records = [new RunRecord(_git.CommonDirectory!, "opencode/fix/run", _runWorktree, DateTimeOffset.UtcNow)];
        _branches.Branches[_runWorktree] = "opencode/fix/run";

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", agentWorktree: _otherWorktree));

        Assert.Equal(HookRunContextSource.EventWorktree, context.Source);
        Assert.Equal(_otherWorktree, context.Worktree);
    }

    [Fact]
    public void ThePayloadCwdStandsWhenNoRunIsLiveAndNoWorktreeIsNamed()
    {
        HookRunContext context = Resolve("TASK: T1");

        Assert.Equal(HookRunContextSource.PayloadCwd, context.Source);
        Assert.Equal(_main, context.Worktree);
        Assert.Null(context.ActiveRun);
    }

    /// <summary>
    /// A harness that sends its directory as a relative path means it relative to the directory the hook process
    /// started in. Resolving one against itself cannot anchor anything: the path seam refuses a base that is not
    /// fully qualified, so the payload's own directory was dropped and the process directory anchored instead.
    /// </summary>
    [Fact]
    public void ARelativePayloadCwdAnchorsTheRepositoryItResolvesTo()
    {
        string elsewhere = ArbiterTestRoots.Under("kw", "elsewhere");
        _files.CreateDirectory(elsewhere);
        FakeProcessEnvironment environment = new(
            currentDirectory: elsewhere, homeDirectory: ArbiterTestRoots.Under("home"));
        HookRunContextResolver resolver = new(
            _git,
            new ContainedPlanFileResolver(),
            _planRoots,
            _files,
            _paths,
            environment,
            _runs,
            _branches);

        HookRunContext context = resolver.Resolve(new HookRunContextRequest(
            "copilot-cli",
            WorkspaceRoot: null,
            EventCwd: $"..{Path.DirectorySeparatorChar}repo",
            ProcessDirectory: elsewhere,
            AgentWorktree: null,
            Prompt: "TASK: T1",
            HomeDirectory: ArbiterTestRoots.Under("home")));

        Assert.Equal(_main, context.RepositoryRoot);
        Assert.Equal(_main, context.Worktree);
    }

    [Fact]
    public void ACandidateOutsideTheRepositoryAndItsWorktreesIsRefusedWithItsReason()
    {
        string outside = ArbiterTestRoots.Under("kw", "outside");
        _files.CreateDirectory(outside);

        HookRunContext context = _resolver.Resolve(Request("TASK: T1", agentWorktree: outside));

        Assert.Equal(_main, context.Worktree);
        Assert.Equal(HookRunContextSource.PayloadCwd, context.Source);
        Assert.Contains(context.Notes, note => note.Contains("outside this repository", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunRecordWhoseWorktreeLeftTheRepositoryIsNotUsed()
    {
        string outside = ArbiterTestRoots.Under("kw", "gone");
        _runs.Records = [new RunRecord(_git.CommonDirectory!, "opencode/fix/run", outside, DateTimeOffset.UtcNow)];

        HookRunContext context = Resolve("TASK: T1");

        Assert.Equal(_main, context.Worktree);
        Assert.Null(context.ActiveRun);
        Assert.Contains(context.Notes, note => note.Contains("not a worktree of this repository", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingTaskFileIsAnAbsenceThatNamesThePathTried()
    {
        WritePlan(_runWorktree, "docs/plans/2026-11-18-run.md", withTaskFile: false);

        HookRunContext context = Resolve("PLAN_FILE: docs/plans/2026-11-18-run.md\nTASK: T1");

        Assert.True(context.PlanExists);
        Assert.False(context.HasTaskFile);
        Assert.Contains(
            context.Notes,
            note => note.Contains("task file", StringComparison.Ordinal)
                && note.Contains(Path.Combine(_runWorktree, "docs", "plans", "2026-11-18-run.tasks.yml"), StringComparison.Ordinal)
                && note.Contains("tried", StringComparison.Ordinal));
    }

    [Fact]
    public void APlanThatEscapesTheRepositoryResolvesToNoPlanWithItsReason()
    {
        HookRunContext context = Resolve("PLAN_FILE: ../elsewhere/plan.md\nTASK: T1");

        Assert.False(context.PlanExists);
        Assert.Null(context.PlanPath);
        Assert.Contains(context.Notes, note => note.Contains("names nothing inside", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectPlanReadsAReturnSPlanFromTheValidatedWorktreeWithoutReadingGitAgain()
    {
        WritePlan(_otherWorktree, "docs/plans/2026-11-18-other.md");
        HookRunContext context = Resolve("TASK: T1");

        HookRunContext selected = _resolver.SelectPlan(context, "docs/plans/2026-11-18-other.md", null);

        Assert.Equal(_otherWorktree, selected.Worktree);
        Assert.Equal(Path.Combine(_otherWorktree, "docs", "plans", "2026-11-18-other.md"), selected.PlanPath);
    }

    /// <summary>
    /// A payload cwd inside one of this repository's worktrees, whose own git directory git cannot name, is refused:
    /// the resolver only anchors where git attested the repository, so plan and git reads cannot land where git never
    /// answered.
    /// </summary>
    [Fact]
    public void APayloadCwdGitCannotAttestIsRefused()
    {
        string target = Path.Combine(_runWorktree, "src");
        _files.CreateDirectory(target);
        _git.CommonDirectories[target] = null;

        HookRunContext context = _resolver.Resolve(
            new HookRunContextRequest("copilot-cli", _main, target, _main, null, "TASK: T1", ArbiterTestRoots.Under("home")));

        Assert.Equal(_main, context.Worktree);
        Assert.Contains(context.Notes, note => note.Contains("git could not name its repository", StringComparison.Ordinal));
    }

    private HookRunContext Resolve(string prompt) => _resolver.Resolve(Request(prompt));

    private HookRunContextRequest Request(string prompt, string? agentWorktree = null) =>
        new("copilot-cli", null, _main, _main, agentWorktree, prompt, ArbiterTestRoots.Under("home"));

    /// <summary>A request whose leading cd target is read from <paramref name="command" /> by the guard's own parser.</summary>
    private HookRunContextRequest Request(string prompt, string? cwd, string? agentWorktree = null, string? command = null) =>
        new(
            "copilot-cli",
            null,
            cwd,
            _main,
            agentWorktree,
            prompt,
            ArbiterTestRoots.Under("home"),
            WorkerGuardRules.LeadingChangeDirectory(command, ArbiterTestRoots.Under("home")));

    /// <summary>Run A is older and works in the run worktree; run B is newer and works in the other worktree.</summary>
    private void TwoLiveRuns()
    {
        _runs.Records =
        [
            new RunRecord(_git.CommonDirectory!, "run/a", _runWorktree, DateTimeOffset.UtcNow.AddHours(-2)),
            new RunRecord(_git.CommonDirectory!, "run/b", _otherWorktree, DateTimeOffset.UtcNow),
        ];
        _branches.Branches[_runWorktree] = "run/a";
        _branches.Branches[_otherWorktree] = "run/b";
    }

    private void WritePlan(string worktree, string relative, bool withTaskFile = true)
    {
        string plan = Path.Combine(worktree, relative.Replace('/', Path.DirectorySeparatorChar));
        _files.CreateDirectory(Path.GetDirectoryName(plan)!);
        _files.WriteAllText(plan, "# Plan\n");
        if (withTaskFile)
        {
            string taskFile = plan[..^".md".Length] + ".tasks.yml";
            _files.WriteAllText(taskFile, "schema: 2\n");
        }
    }
}
