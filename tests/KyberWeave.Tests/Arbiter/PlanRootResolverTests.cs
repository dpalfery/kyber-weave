using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The worktree a dispatch's plan is found in, decided from the file, path and git seams alone: no test needs a
/// checkout, and the git call is replaced by the worktree list the resolver already accepts.
/// </summary>
public sealed class PlanRootResolverTests
{
    private const string HookRoot = "/repo";
    private const string Worktree = "/repo.worktrees/run";
    private const string OtherWorktree = "/repo.worktrees/other";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFileSystem _files;
    private readonly FakePathResolver _paths;
    private readonly ContainedPlanFileResolver _planPaths = new();

    public PlanRootResolverTests()
    {
        _files = new InMemoryFileSystem(_time);
        _paths = new FakePathResolver(_files);
    }

    [Fact]
    public void Resolve_ThePlanLivesInOneOtherWorktree_ReturnsThatWorktree()
    {
        WritePlan(Worktree, "# Plan\n");

        string root = Resolver().Resolve(HookRoot, Prompt(), _ => [HookRoot, Worktree]);

        Assert.Equal(Worktree, root);
    }

    [Fact]
    public void Resolve_NoOtherWorktreeHoldsThePlan_KeepsTheHookRoot()
    {
        string root = Resolver().Resolve(HookRoot, Prompt(), _ => [HookRoot, Worktree]);

        Assert.Equal(HookRoot, root);
    }

    [Fact]
    public void Resolve_TwoWorktreesHoldThePlan_ReturnsTheCopyWrittenLast()
    {
        WritePlan(OtherWorktree, "# Older\n");
        _time.Advance(TimeSpan.FromMinutes(1));
        WritePlan(Worktree, "# Newer\n");

        string root = Resolver().Resolve(HookRoot, Prompt(), _ => [HookRoot, OtherWorktree, Worktree]);

        Assert.Equal(Worktree, root);
    }

    [Fact]
    public void Resolve_NoPlanButTheTextNamesOneWorktree_ReturnsTheWorktreeItNames()
    {
        _files.CreateDirectory(Worktree);
        _files.CreateDirectory(OtherWorktree);
        string root = Resolver().Resolve(HookRoot, $"Worktree: {OtherWorktree}\n\nWrite the tests.", _ => [HookRoot, Worktree, OtherWorktree]);

        Assert.Equal(OtherWorktree, root);
    }

    [Fact]
    public void Resolve_TwoWorktreesHoldThePlan_AndOnePlansMetadataCannotBeRead_KeepsTheHookRoot()
    {
        WritePlan(OtherWorktree, "# Older\n");
        WritePlan(Worktree, "# Newer\n");
        _files.FailWhen(
            operation => operation.Name == nameof(IFileSystem.GetLastWriteTimeUtc)
                && operation.Path == $"{OtherWorktree}/docs/plans/task.md",
            new UnauthorizedAccessException("denied"));

        string root = Resolver().Resolve(HookRoot, Prompt(), _ => [HookRoot, OtherWorktree, Worktree]);

        Assert.Equal(HookRoot, root);
    }

    [Fact]
    public void Resolve_TheWorktreeListCannotBeRead_KeepsTheHookRoot()
    {
        PlanRootResolver resolver = new(
            new StubGitFacts(failure: new IOException("git could not run")),
            _planPaths,
            _files,
            _paths);

        string root = resolver.Resolve(HookRoot, Prompt());

        Assert.Equal(HookRoot, root);
    }

    [Fact]
    public void Resolve_TheWorktreeListHoldsAnEntryThatIsNoFullPath_SkipsItRatherThanThrowing()
    {
        WritePlan(Worktree, "# Plan\n");
        PlanRootResolver resolver = new(
            new StubGitFacts(["relative/worktree", string.Empty, Worktree]),
            _planPaths,
            _files,
            _paths);

        string root = resolver.Resolve(HookRoot, Prompt());

        Assert.Equal(Worktree, root);
    }

    [Fact]
    public void Resolve_ThePlanHeaderNamesAPathOutsideTheRepository_KeepsTheHookRoot()
    {
        string root = Resolver().Resolve(HookRoot, "PLAN_FILE: /tmp/evil.md\n\nWork on the task.", _ => [HookRoot, Worktree]);

        Assert.Equal(HookRoot, root);
    }

    [Fact]
    public void TheHostRegistersTheResolverOnceWithTheSeamsItsConstructorNeeds()
    {
        IPlanRootResolver first = ArbiterTestServices.Get<IPlanRootResolver>();
        IPlanRootResolver second = ArbiterTestServices.Get<IPlanRootResolver>();

        Assert.IsType<PlanRootResolver>(first);
        Assert.Same(first, second);
        Assert.NotNull(ArbiterTestServices.Get<IFileSystem>());
        Assert.NotNull(ArbiterTestServices.Get<IPathResolver>());
    }

    private void WritePlan(string worktree, string text)
    {
        _files.CreateDirectory($"{worktree}/docs/plans");
        _files.WriteAllText($"{worktree}/docs/plans/task.md", text);
    }

    private PlanRootResolver Resolver() => new(new StubGitFacts(), _planPaths, _files, _paths);

    private static string Prompt() => "PLAN_FILE: docs/plans/task.md\n\nWork on the task.";

    /// <summary>Answers only the worktree list: the resolver asks git nothing else.</summary>
    private sealed class StubGitFacts(IReadOnlyList<string>? worktrees = null, Exception? failure = null) : IGitRepositoryFacts
    {
        public string? GetRoot(string workingDirectory, string gitExecutable = "git") => throw Unsupported();

        public IReadOnlyList<string> ListWorktrees(string workingDirectory, string gitExecutable = "git") =>
            failure is null ? worktrees ?? [] : throw failure;

        public string? GetCommonDirectory(string workingDirectory, string gitExecutable = "git") => throw Unsupported();

        public string? GetCurrentBranch(string workingDirectory, string gitExecutable = "git") => throw Unsupported();

        public bool RefExists(string workingDirectory, string revision, string gitExecutable = "git") => throw Unsupported();

        public string? GetHead(string workingDirectory, string gitExecutable = "git") => throw Unsupported();

        public IReadOnlyDictionary<string, string> GetDirtyStatuses(string workingDirectory, string gitExecutable = "git") => throw Unsupported();

        public IReadOnlyDictionary<string, string> GetBlobIds(string workingDirectory, IEnumerable<string> paths, string gitExecutable = "git") => throw Unsupported();

        public IReadOnlySet<string> GetChangedPathsSinceBase(string workingDirectory, string baseRevision, string gitExecutable = "git") => throw Unsupported();

        public IReadOnlyList<HunkRange> GetHunkRanges(string workingDirectory, string baseRef, string gitExecutable = "git") => throw Unsupported();

        public IReadOnlyList<ChangedHunk>? GetChangedHunks(string workingDirectory, string baseRef, string gitExecutable = "git") => throw Unsupported();

        public ChangeSet? GetChangeSet(string workingDirectory, string baseRef, string gitExecutable = "git") => throw Unsupported();

        public string? GetDiffSummary(
            string workingDirectory,
            string baseRef,
            IReadOnlyCollection<string> changedPaths,
            string gitExecutable = "git") => throw Unsupported();

        public GitSnapshot? CaptureSnapshot(string workingDirectory, string gitExecutable = "git") => throw Unsupported();

        public IReadOnlySet<string> GetChangedPathsSinceSnapshot(string workingDirectory, GitSnapshot snapshot, string gitExecutable = "git") => throw Unsupported();

        private static NotSupportedException Unsupported() =>
            new("The plan root resolver asks git for its worktree list only.");
    }
}
