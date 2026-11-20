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

    public HookRunContextResolverTests()
    {
        _main = ArbiterTestRoots.Under("kw", "repo");
        _runWorktree = ArbiterTestRoots.Under("kw", "run-worktree");
        _otherWorktree = ArbiterTestRoots.Under("kw", "other-worktree");
        foreach (string directory in new[] { _main, _runWorktree, _otherWorktree })
        {
            _files.CreateDirectory(directory);
        }

        _git.CommonDirectory = Path.Combine(_main, ".git");
        _git.Worktrees[_main] = [_main, _runWorktree, _otherWorktree];
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

    [Fact]
    public void OnlyTheNewestRunRecordWhoseBranchIsStillCheckedOutIsUsed()
    {
        _runs.Records =
        [
            new RunRecord(_git.CommonDirectory!, "opencode/old/run", _runWorktree, DateTimeOffset.UtcNow.AddHours(-2)),
            new RunRecord(_git.CommonDirectory!, "opencode/new/run", _otherWorktree, DateTimeOffset.UtcNow),
        ];
        _branches.Branches[_runWorktree] = "opencode/old/run";
        _branches.Branches[_otherWorktree] = "opencode/new/run";

        HookRunContext context = Resolve("TASK: T1");

        Assert.Equal(_otherWorktree, context.Worktree);
        Assert.Equal("opencode/new/run", context.ActiveRun?.Branch);
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
