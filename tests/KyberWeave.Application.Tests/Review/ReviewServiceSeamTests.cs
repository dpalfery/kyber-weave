using KyberWeave.Application.Review;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The work tree, cycle and seats services and the plan-task target service resolve a path a caller spelled
/// through the path seam, and read plans and directories through the file seam: a relative spelling names a place
/// under the injected working directory, and a plan larger than the read bound is not loaded.
/// </summary>
public sealed class ReviewServiceSeamTests
{
    private const int PlanReadBound = 8 * 1024 * 1024;

    private readonly InMemoryFileSystem _files = new();

    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/work");

    private FakePathResolver Paths => new(_files);

    private static KyberWeaveConfig Config() => new() { Arbiter = ArbiterConfigDefaults.Product };

    [Fact]
    public void TheWorkTreeOfARelativePathIsTheNearestAncestorWithAGitEntryUnderTheWorkingDirectory()
    {
        _files.CreateDirectory("/work/repo/.git");
        _files.CreateDirectory("/work/repo/src");

        string tree = WorkTrees().Containing("repo/src");

        Assert.Equal("/work/repo", tree);
    }

    [Fact]
    public void ALinkedWorktreeMarkedByAGitFileIsItsOwnWorkTree()
    {
        _files.CreateDirectory("/work/repo/.git");
        _files.CreateDirectory("/work/repo/wt/src");
        _files.WriteAllText("/work/repo/wt/.git", "gitdir: ../.git/worktrees/wt");

        Assert.Equal("/work/repo/wt", WorkTrees().Containing("repo/wt/src"));
    }

    [Fact]
    public void APathInNoWorkTreeIsItsOwnFullPath()
    {
        _files.CreateDirectory("/work/plain");

        Assert.Equal("/work/plain", WorkTrees().Containing("plain"));
    }

    [Fact]
    public void TheCycleCommandLoadsTheConfigurationForTheRootARelativePathResolvesTo()
    {
        RecordingConfigs configs = new();

        ReviewCommandResult result = Cycle(configs).Run(new ReviewCycleRequest("repo", null, null, null, false), TextWriter.Null);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/work/repo", configs.Root);
    }

    [Fact]
    public void TheCycleCommandsPrintedGateCommandNamesAnAbsoluteReportPath()
    {
        _files.CreateDirectory("/work/repo/.git");
        FakeReviewWorkTreeService trees = new() { WorkingDirectory = "/work" };
        FileReviewCycleStore cycles = new(_files, Paths, _environment);
        ReviewCycleService service = new(trees, cycles, new FakeReviewCycleSelector(), new LoadingConfigs(), Paths, _environment);
        StringWriter output = new();

        ReviewCommandResult result = service.Run(new ReviewCycleRequest("repo", null, "T1", null, false), output);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "run: kyber-weave review gates /work/repo --council --out " +
            "/work/repo/.agent_scratchpad/T1/review/cycle-1/gates.json",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheCycleCommandKeepsAnAbsolutePathWhateverTheWorkingDirectoryIs()
    {
        RecordingConfigs configs = new();

        _ = Cycle(configs).Run(new ReviewCycleRequest("/elsewhere/repo", null, null, null, false), TextWriter.Null);

        Assert.Equal("/elsewhere/repo", configs.Root);
    }

    [Fact]
    public void TheSeatsCommandLoadsTheConfigurationForTheRootARelativePathResolvesTo()
    {
        RecordingConfigs configs = new();

        ReviewCommandResult result = Seats(configs).Prepare(
            new ReviewSeatsRequest("repo", null, null, null, false, false, false),
            (_, _) => throw new InvalidOperationException("unexpected call"),
            new FakeProcessExecutor(),
            TextWriter.Null);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/work/repo", configs.Root);
    }

    [Fact]
    public async Task TheSeatsCommandAsyncLoadsTheConfigurationForTheRootARelativePathResolvesTo()
    {
        RecordingConfigs configs = new();

        ReviewCommandResult result = await Seats(configs).PrepareAsync(
            new ReviewSeatsRequest("repo", null, null, null, false, false, false),
            (_, _) => throw new InvalidOperationException("unexpected call"),
            new ScriptedProcessRunner(),
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/work/repo", configs.Root);
    }

    [Fact]
    public async Task TheGatesCommandAsyncLoadsTheConfigurationForTheRootARelativePathResolvesTo()
    {
        RecordingConfigs configs = new();

        ReviewCommandResult result = await Gates(configs).RunAsync(
            new ReviewGatesRequest(
                "repo", null, null, null, null, [], [], false, false, false, false, false, null, false, false, false, false),
            new ScriptedProcessRunner(),
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/work/repo", configs.Root);
    }

    [Fact]
    public async Task TheVerdictCommandAsyncLoadsTheConfigurationForTheRootARelativePathResolvesTo()
    {
        RecordingConfigs configs = new();

        ReviewVerdictResult result = await Verdict(configs).DecideAsync(
            new ReviewVerdictRequest(
                "repo", null, null, null, null, null, null, null, null, [], [], false, false, "kyber-weave review verdict ."),
            CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/work/repo", configs.Root);
    }

    [Fact]
    public async Task TheDecisionModelFactoryAsyncSwitchesOffWithoutAskingForKey()
    {
        ReviewDecisionModelFactory factory = new(null!, null!, null!, null!, TimeProvider.System, new FakeIdGenerator());

        IReviewDecisionModel model = await factory.CreateForRepositoryAsync(
            "/work/repo", Config(), switchedOff: true, cachePath: null, CancellationToken.None);

        Assert.False(model.Configured);
    }

    [Fact]
    public async Task ATaskInAnotherWorktreeIsFoundAsyncWhenTheWorktreeIsSpelledRelativeToTheWorkingDirectory()
    {
        _files.CreateDirectory("/work/wt");
        PlanTaskTargetService service = Targets(worktrees: ["wt"], activeIn: "/work/wt");

        PlanTaskTargetLookup lookup = await service.FindAsync("repo", Config(), "T1", CancellationToken.None);

        Assert.NotNull(lookup.Match);
        Assert.Null(lookup.Reason);
        Assert.Equal("docs/plan.md", lookup.Match.Plan);
    }

    [Fact]
    public async Task ATaskIsStillFoundAsyncInThisCheckoutsPlanThatHasMovedIntoReview()
    {
        _files.CreateDirectory("/work/repo");
        PlanTaskTargetService service = Targets(worktrees: [], activeIn: null, reviewPlans: ["docs/plan.md"]);

        PlanTaskTargetLookup lookup = await service.FindAsync("repo", Config(), "T1", CancellationToken.None);

        Assert.Null(lookup.Reason);
        Assert.NotNull(lookup.Match);
        Assert.Equal("docs/plan.md", lookup.Match.Plan);
        Assert.Equal("T1", lookup.Match.Entry.Id);
    }

    [Fact]
    public void ATaskInAnotherWorktreeIsFoundWhenTheWorktreeIsSpelledRelativeToTheWorkingDirectory()
    {
        _files.CreateDirectory("/work/wt");
        PlanTaskTargetService service = Targets(worktrees: ["wt"], activeIn: "/work/wt");

        PlanTaskTargetMatch? match = service.Find("repo", Config(), "T1", out string? reason);

        Assert.NotNull(match);
        Assert.Null(reason);
        Assert.Equal("docs/plan.md", match.Plan);
    }

    [Fact]
    public void AWorktreeThatIsNotADirectoryIsNotSearched()
    {
        PlanTaskTargetService service = Targets(worktrees: ["wt"], activeIn: "/work/wt");

        PlanTaskTargetMatch? match = service.Find("repo", Config(), "T1", out string? reason);

        Assert.Null(match);
        Assert.NotNull(reason);
    }

    [Fact]
    public void TheCheckoutsOwnWorktreeEntryIsNotSearchedAgainWhateverItsSpelling()
    {
        _files.CreateDirectory("/work/repo");
        PlanTaskTargetService service = Targets(worktrees: ["repo/"], activeIn: "/work/repo");

        PlanTaskTargetMatch? match = service.Find("repo", Config(), "T1", out _);

        Assert.Null(match);
    }

    [Fact]
    public void ATaskIsStillFoundInThisCheckoutsPlanThatHasMovedIntoReview()
    {
        _files.CreateDirectory("/work/repo");
        PlanTaskTargetService service = Targets(worktrees: [], activeIn: null, reviewPlans: ["docs/plan.md"]);

        PlanTaskTargetMatch? match = service.Find("repo", Config(), "T1", out string? reason);

        Assert.Null(reason);
        Assert.NotNull(match);
        Assert.Equal("docs/plan.md", match.Plan);
        Assert.Equal("T1", match.Entry.Id);
    }

    [Fact]
    public void ThePlansContractRowIsReadThroughTheFileSeam()
    {
        _files.CreateDirectory("/work/repo/docs");
        _files.WriteAllText("/work/repo/docs/plan.md", PlanTaskFileProbe.PlanMarkdown());

        PlanContractRow? row = Targets([], null).ContractRow("/work/repo", "docs/plan.md", "T1");

        Assert.NotNull(row);
        Assert.Equal("T1", row.TaskId);
    }

    [Fact]
    public void APlanLargerThanTheReadBoundYieldsNoContractRow()
    {
        _files.CreateDirectory("/work/repo/docs");
        _files.WriteAllBytes("/work/repo/docs/plan.md", new byte[PlanReadBound + 1]);

        Assert.Null(Targets([], null).ContractRow("/work/repo", "docs/plan.md", "T1"));
    }

    [Fact]
    public void APlanAtTheReadBoundIsStillRead()
    {
        byte[] plan = new byte[PlanReadBound];
        byte[] markdown = System.Text.Encoding.UTF8.GetBytes(PlanTaskFileProbe.PlanMarkdown());
        Array.Fill(plan, (byte)' ');
        markdown.CopyTo(plan, 0);
        _files.CreateDirectory("/work/repo/docs");
        _files.WriteAllBytes("/work/repo/docs/plan.md", plan);

        Assert.NotNull(Targets([], null).ContractRow("/work/repo", "docs/plan.md", "T1"));
    }

    private ReviewWorkTreeService WorkTrees() =>
        new(null!, null!, _files, null!, null!, Paths, _environment);

    private ReviewCycleService Cycle(IConfigLoadService configs) =>
        new(null!, null!, null!, configs, Paths, _environment);

    private ReviewSeatsService Seats(IConfigLoadService configs) =>
        new(null!, null!, null!, null!, null!, null!, null!, _files, _environment, null!, configs, null!, Paths);

    private ReviewGatesService Gates(IConfigLoadService configs) =>
        new(
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            _files, _environment, Paths, null!, configs, null!, null!);

    private ReviewVerdictService Verdict(IConfigLoadService configs) =>
        new(
            configs, null!, null!, null!, null!, null!, null!, _files, null!, null!, null!,
            Paths, _environment, TimeProvider.System);

    /// <summary>
    /// A target service over the shared fakes. The fake worktree list answers for the checkout named
    /// <c>repo</c>; the plan it finds is active only in <paramref name="activeIn"/>, and is one of the plans
    /// still in review when <paramref name="reviewPlans"/> names them.
    /// </summary>
    private PlanTaskTargetService Targets(string[] worktrees, string? activeIn, string[]? reviewPlans = null)
    {
        PlanTaskFile file = new(1, Tasks: [new PlanTaskFileEntry(Id: "T1")]);
        FakePlanTaskService service = new()
        {
            PlansByRoot = activeIn is null ? [] : new(StringComparer.Ordinal) { [activeIn] = ["docs/plan.md"] },
            ReviewPlansByRoot = reviewPlans is null || reviewPlans.Length == 0
                ? []
                : new(StringComparer.Ordinal) { ["repo"] = reviewPlans },
            Outcomes = new(StringComparer.Ordinal)
            {
                ["docs/plan.md"] = new PlanTaskFileOutcome(new DiagnosticReport(), "docs/plan.tasks.json", null, null, 1, false, file)
            }
        };
        FakeGitRepositoryFacts git = new()
        {
            Worktrees = new(StringComparer.Ordinal) { ["repo"] = worktrees }
        };

        return new PlanTaskTargetService(service, null!, git, _files, Paths, _environment);
    }

    /// <summary>Answers a product configuration, so the command runs past the load and reaches the lines it prints.</summary>
    private sealed class LoadingConfigs : IConfigLoadService
    {
        public bool TryLoad(
            string repoRoot,
            string? configPath,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out string? resolvedConfigPath)
        {
            config = new KyberWeaveConfig { Arbiter = ArbiterConfigDefaults.Product };
            resolvedConfigPath = null;
            return true;
        }

        public bool TryLoadWithDocsRoots(
            string repoRoot,
            string? configPath,
            IReadOnlyList<string> docsRoots,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out OntologyConfig ontology) =>
            throw new InvalidOperationException("unexpected call");
    }

    /// <summary>Records the root it was asked to load and refuses, so the command stops before any other seam is used.</summary>
    private sealed class RecordingConfigs : IConfigLoadService
    {
        public string? Root { get; private set; }

        public bool TryLoad(
            string repoRoot,
            string? configPath,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out string? resolvedConfigPath)
        {
            Root = repoRoot;
            config = new KyberWeaveConfig { Arbiter = ArbiterConfigDefaults.Product };
            resolvedConfigPath = null;
            report.Add(new Diagnostic("KW-TEST-002", Severity.Error, "refused by the test", "review"));
            return false;
        }

        public bool TryLoadWithDocsRoots(
            string repoRoot,
            string? configPath,
            IReadOnlyList<string> docsRoots,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out OntologyConfig ontology) =>
            throw new InvalidOperationException("unexpected call");
    }
}
