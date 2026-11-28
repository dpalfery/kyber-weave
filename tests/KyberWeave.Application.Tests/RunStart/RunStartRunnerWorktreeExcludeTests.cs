using System.Diagnostics;
using KyberWeave.Application.RunStart;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.RunStart;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.RunStart;

/// <summary>
///     The local exclude that keeps the worktree folder out of the checkout's status, and the order
///     <see cref="RunStartService" /> writes it in, over the in-memory file system and a scripted git.
/// </summary>
public sealed class RunStartRunnerWorktreeExcludeTests
{
    private const string ExcludeLine = ".agent_worktrees/";
    private const string ExcludeAnswer = ".git/info/exclude";

    private readonly InMemoryFileSystem _files = new();

    private string Repository => Path.Combine(_files.Root, "repo");

    private string ExcludePath => Path.Combine(Repository, ".git", "info", "exclude");

    /// <summary>Writes an exclude file as an existing checkout has one: inside its info folder, which git creates.</summary>
    private void SeedExclude(string contents)
    {
        _files.CreateDirectory(Path.GetDirectoryName(ExcludePath)!);
        _files.WriteAllText(ExcludePath, contents);
    }

    /// <summary>An exclude file that does not exist yet is created holding the line.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AnAbsentExcludeFileIsCreatedWithTheLine()
    {
        RunStartRunner runner = Runner(ExcludeAnswer);

        GitOutcome outcome = await runner.EnsureWorktreeFolderExcludedAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal(ExcludeLine + "\n", _files.ReadAllText(ExcludePath));
    }

    /// <summary>An absolute answer, as a linked worktree gets, is written where git named it.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AnAbsoluteAnswerFromALinkedWorktreeIsUsedAsGiven()
    {
        string common = Path.Combine(_files.Root, "main", ".git", "info", "exclude");
        RunStartRunner runner = Runner(common);

        GitOutcome outcome = await runner.EnsureWorktreeFolderExcludedAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal(ExcludeLine + "\n", _files.ReadAllText(common));
        Assert.False(_files.FileExists(ExcludePath));
    }

    /// <summary>An existing file keeps its content and gains the line after it.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AFileWithoutTheLineGetsTheLineAppendedAfterItsContent()
    {
        SeedExclude("*.log\n");

        GitOutcome outcome = await Runner(ExcludeAnswer)
            .EnsureWorktreeFolderExcludedAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal("*.log\n" + ExcludeLine + "\n", _files.ReadAllText(ExcludePath));
    }

    /// <summary>A file that ends without a newline gets one before the appended line.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AFileNotEndingInANewlineGetsOneBeforeTheLine()
    {
        SeedExclude("*.log");

        GitOutcome outcome = await Runner(ExcludeAnswer)
            .EnsureWorktreeFolderExcludedAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal("*.log\n" + ExcludeLine + "\n", _files.ReadAllText(ExcludePath));
    }

    /// <summary>A line already present, even with surrounding spaces, is left exactly as it is.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AFileAlreadyHoldingTheLineWithSurroundingSpacesIsUnchanged()
    {
        const string original = "*.log\n  .agent_worktrees/  \n";
        SeedExclude(original);

        GitOutcome outcome = await Runner(ExcludeAnswer)
            .EnsureWorktreeFolderExcludedAsync(TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal(original, _files.ReadAllText(ExcludePath));
    }

    /// <summary>A write that fails reports the failure and leaves the file as it was.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AWriteFailureReturnsTheFailure()
    {
        SeedExclude("*.log\n");
        _files.FailWhen(operation => operation.Name == "WriteAllTextAtomic", new IOException("disk full"));

        GitOutcome outcome = await Runner(ExcludeAnswer)
            .EnsureWorktreeFolderExcludedAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("disk full", outcome.FirstErrorLine, StringComparison.Ordinal);
        Assert.Equal("*.log\n", _files.ReadAllText(ExcludePath));
    }

    /// <summary>A write the file system refuses reports the refusal and leaves the file as it was.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AnUnauthorizedWriteReturnsTheFailure()
    {
        SeedExclude("*.log\n");
        _files.FailWhen(operation => operation.Name == "WriteAllTextAtomic", new UnauthorizedAccessException("denied"));

        GitOutcome outcome = await Runner(ExcludeAnswer)
            .EnsureWorktreeFolderExcludedAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("denied", outcome.FirstErrorLine, StringComparison.Ordinal);
        Assert.Equal("*.log\n", _files.ReadAllText(ExcludePath));
    }

    /// <summary>A git that cannot name the exclude file hands back its own failure.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AGitFailureToNameTheExcludeFileReturnsGitsOutcome()
    {
        RunStartRunner runner = Runner((_, _) => new GitOutcome(128, string.Empty, "fatal: not a git repository"));

        GitOutcome outcome = await runner.EnsureWorktreeFolderExcludedAsync(TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("fatal", outcome.FirstErrorLine, StringComparison.Ordinal);
    }

    /// <summary>The service writes the exclude before it names the worktree folder.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task TheServiceWritesTheExcludeBeforeItNamesTheWorktreeFolder()
    {
        OrderRunner runner = new(Repository) { Dirty = true };

        RunStartOutcome outcome = await Service(runner).StartAsync(Request());

        Assert.Equal(RunStartStatus.ToolFailure, outcome.Status);
        int exclude = runner.Calls.IndexOf("EnsureWorktreeFolderExcluded");
        int folder = runner.Calls.IndexOf("WorktreePathFor");
        Assert.True(exclude >= 0, "the exclude is written on a run that creates a worktree folder");
        Assert.True(folder > exclude, "the folder is named only after the exclude is written");
    }

    /// <summary>A failed exclude stops the run with the remedy and creates no worktree.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AFailedExcludeStopsTheRunWithTheRemedyAndCreatesNoWorktree()
    {
        OrderRunner runner = new(Repository)
        {
            Dirty = true,
            ExcludeResult = new GitOutcome(1, string.Empty, "fatal: cannot write"),
        };

        RunStartOutcome outcome = await Service(runner).StartAsync(Request());

        Assert.Equal(RunStartStatus.ToolFailure, outcome.Status);
        Assert.Contains(
            outcome.Messages,
            message => message.Kind == RunStartMessageKind.Remedy
                && message.Text.Contains(ExcludeLine, StringComparison.Ordinal)
                && message.Text.Contains(".git/info/exclude", StringComparison.Ordinal));
        Assert.DoesNotContain("WorktreePathFor", runner.Calls);
        Assert.DoesNotContain("CreateBranchInWorktree", runner.Calls);
    }

    /// <summary>An in-place run never writes the exclude, because no worktree folder is made.</summary>
    /// <returns>The completed test.</returns>
    [Fact]
    public async Task AnInPlaceRunNeverWritesTheExclude()
    {
        OrderRunner runner = new(Repository)
        {
            Dirty = false,
            CreateInPlaceResult = new GitOutcome(1, string.Empty, "stop after the branch decision"),
        };

        await Service(runner).StartAsync(Request());

        Assert.Contains("CreateBranchInPlace", runner.Calls);
        Assert.DoesNotContain("EnsureWorktreeFolderExcluded", runner.Calls);
    }

    private RunStartRunner Runner(string answer) => Runner((_, _) => new GitOutcome(0, answer + "\n", string.Empty));

    private RunStartRunner Runner(Func<string, IReadOnlyList<string>, GitOutcome> git) =>
        new(
            new ScriptedGit(git),
            _files,
            new FakeProcessEnvironment(currentDirectory: _files.Root),
            new FakePathResolver(_files),
            Repository);

    private RunStartService Service(IRunStartRunner runner) =>
        new(
            new StubRunners(runner),
            new NoIndexGuard(),
            new FakeRunStateStore(),
            _files,
            new FakeProcessEnvironment(currentDirectory: _files.Root),
            new FakeGitRepositoryFacts(),
            new NoProcesses(),
            TimeProvider.System);

    private RunStartRequest Request() =>
        new(Repository, "claude/fix/x", null, KyberWeaveConfigDefaults.Product, null, null);

    /// <summary>A git runner that answers each invocation from the script the test supplies.</summary>
    private sealed class ScriptedGit(Func<string, IReadOnlyList<string>, GitOutcome> run) : IGitCommandRunner
    {
        public GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments) => run(workingDirectory, arguments);

        public Task<GitOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(run(workingDirectory, arguments));
    }

    private sealed class StubRunners(IRunStartRunner runner) : IRunStartRunnerFactory
    {
        public IRunStartRunner Open(string repositoryRoot) => runner;
    }

    private sealed class NoIndexGuard : IWorktreeIndexGuard
    {
        public WorktreeIndexOutcome Ensure(
            string root,
            DoctorOptions options,
            IProcessExecutor executor,
            string lockDirectory,
            CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles) =>
            throw new NotSupportedException();

        public Task<WorktreeIndexOutcome> EnsureAsync(
            string root,
            DoctorOptions options,
            IProcessRunner executor,
            string lockDirectory,
            CodeGraphRestore restore = CodeGraphRestore.AllWrittenFiles,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool IsAlive(int processId) => false;

        public string? FindOnPath(string program) => null;
    }

    /// <summary>A runner that answers the calls Start makes before the folder step, and records the order of the folder steps.</summary>
    private sealed class OrderRunner(string root) : IRunStartRunner
    {
        public List<string> Calls { get; } = [];

        public bool Dirty { get; init; }

        public GitOutcome ExcludeResult { get; init; } = new(0, string.Empty, string.Empty);

        public GitOutcome CreateInPlaceResult { get; init; } = new(0, string.Empty, string.Empty);

        public string RepositoryRoot => root;

        public Task<GitOutcome> GitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitOutcome(0, string.Empty, string.Empty));

        public Task<string?> DetectRepositoryRootAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(root);

        public Task<GitOutcome> CheckoutStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitOutcome(0, Dirty ? " M README.md\n" : string.Empty, string.Empty));

        public Task<bool?> CheckoutIsCleanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<bool?>(!Dirty);

        public Task<GitOutcome> WorktreeListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitOutcome(0, "worktree " + root + "\n", string.Empty));

        public Task<bool?> AnotherWorktreeIsAttachedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<bool?>(false);

        public Task<string?> OriginHeadSymbolicRefAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> ResolveRevisionAsync(string revision, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("abc123");

        public Task<bool> HasLocalBranchAsync(string branch, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> HasRemoteBranchAsync(string branch, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<string?> DescribeAsync(string revision, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("v1");

        public Task<(int? Ahead, int? Behind)> DivergenceAsync(
            string left, string right, CancellationToken cancellationToken = default) =>
            Task.FromResult<(int? Ahead, int? Behind)>((0, 0));

        public Task<GitOutcome> CommitMessagesAsync(
            string baseRef, string tip, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> OriginAddressAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public bool CodeGraphBinaryIsAvailable() => true;

        public Task<GitOutcome> FetchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitOutcome(0, string.Empty, string.Empty));

        public Task<GitOutcome> CreateBranchInPlaceAsync(
            string branch, string startRef, CancellationToken cancellationToken = default)
        {
            Calls.Add("CreateBranchInPlace");
            return Task.FromResult(CreateInPlaceResult);
        }

        public Task<GitOutcome> SwitchToBranchAsync(string branch, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string WorktreePathFor(string branch, DateTimeOffset startedAt)
        {
            Calls.Add("WorktreePathFor");
            return Path.Combine(root, ".agent_worktrees", "folder");
        }

        public Task<GitOutcome> EnsureWorktreeFolderExcludedAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("EnsureWorktreeFolderExcluded");
            return Task.FromResult(ExcludeResult);
        }

        public void ReleaseWorktreeClaim(string worktreePath)
        {
        }

        public Task<GitOutcome> CreateBranchInWorktreeAsync(
            string branch, string startRef, string worktreePath, CancellationToken cancellationToken = default)
        {
            Calls.Add("CreateBranchInWorktree");
            return Task.FromResult(new GitOutcome(1, string.Empty, "stop after the folder decision"));
        }

        public Task<GitOutcome> AddWorktreeForBranchAsync(
            string branch, string worktreePath, string? startRef = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
