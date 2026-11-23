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

    [Fact]
    public void AnAbsentExcludeFileIsCreatedWithTheLine()
    {
        RunStartRunner runner = Runner(ExcludeAnswer);

        GitOutcome outcome = runner.EnsureWorktreeFolderExcluded();

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal(ExcludeLine + "\n", _files.ReadAllText(ExcludePath));
    }

    [Fact]
    public void AnAbsoluteAnswerFromALinkedWorktreeIsUsedAsGiven()
    {
        string common = Path.Combine(_files.Root, "main", ".git", "info", "exclude");
        RunStartRunner runner = Runner(common);

        GitOutcome outcome = runner.EnsureWorktreeFolderExcluded();

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal(ExcludeLine + "\n", _files.ReadAllText(common));
        Assert.False(_files.FileExists(ExcludePath));
    }

    [Fact]
    public void AFileWithoutTheLineGetsTheLineAppendedAfterItsContent()
    {
        SeedExclude("*.log\n");

        GitOutcome outcome = Runner(ExcludeAnswer).EnsureWorktreeFolderExcluded();

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal("*.log\n" + ExcludeLine + "\n", _files.ReadAllText(ExcludePath));
    }

    [Fact]
    public void AFileNotEndingInANewlineGetsOneBeforeTheLine()
    {
        SeedExclude("*.log");

        GitOutcome outcome = Runner(ExcludeAnswer).EnsureWorktreeFolderExcluded();

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal("*.log\n" + ExcludeLine + "\n", _files.ReadAllText(ExcludePath));
    }

    [Fact]
    public void AFileAlreadyHoldingTheLineWithSurroundingSpacesIsUnchanged()
    {
        const string original = "*.log\n  .agent_worktrees/  \n";
        SeedExclude(original);

        GitOutcome outcome = Runner(ExcludeAnswer).EnsureWorktreeFolderExcluded();

        Assert.True(outcome.Succeeded, outcome.FirstErrorLine);
        Assert.Equal(original, _files.ReadAllText(ExcludePath));
    }

    [Fact]
    public void AWriteFailureReturnsTheFailure()
    {
        SeedExclude("*.log\n");
        _files.FailWhen(operation => operation.Name == "WriteAllText", new IOException("disk full"));

        GitOutcome outcome = Runner(ExcludeAnswer).EnsureWorktreeFolderExcluded();

        Assert.False(outcome.Succeeded);
        Assert.Contains("disk full", outcome.FirstErrorLine, StringComparison.Ordinal);
        Assert.Equal("*.log\n", _files.ReadAllText(ExcludePath));
    }

    [Fact]
    public void AnUnauthorizedWriteReturnsTheFailure()
    {
        SeedExclude("*.log\n");
        _files.FailWhen(operation => operation.Name == "WriteAllText", new UnauthorizedAccessException("denied"));

        GitOutcome outcome = Runner(ExcludeAnswer).EnsureWorktreeFolderExcluded();

        Assert.False(outcome.Succeeded);
        Assert.Contains("denied", outcome.FirstErrorLine, StringComparison.Ordinal);
    }

    [Fact]
    public void AGitFailureToNameTheExcludeFileReturnsGitsOutcome()
    {
        RunStartRunner runner = Runner((_, _) => new GitOutcome(128, string.Empty, "fatal: not a git repository"));

        GitOutcome outcome = runner.EnsureWorktreeFolderExcluded();

        Assert.False(outcome.Succeeded);
        Assert.Contains("fatal", outcome.FirstErrorLine, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceWritesTheExcludeBeforeItNamesTheWorktreeFolder()
    {
        OrderRunner runner = new(Repository) { Dirty = true };

        RunStartOutcome outcome = Service(runner).Start(Request());

        Assert.Equal(RunStartStatus.ToolFailure, outcome.Status);
        int exclude = runner.Calls.IndexOf("EnsureWorktreeFolderExcluded");
        int folder = runner.Calls.IndexOf("WorktreePathFor");
        Assert.True(exclude >= 0, "the exclude is written on a run that creates a worktree folder");
        Assert.True(folder > exclude, "the folder is named only after the exclude is written");
    }

    [Fact]
    public void AFailedExcludeStopsTheRunWithTheRemedyAndCreatesNoWorktree()
    {
        OrderRunner runner = new(Repository)
        {
            Dirty = true,
            ExcludeResult = new GitOutcome(1, string.Empty, "fatal: cannot write"),
        };

        RunStartOutcome outcome = Service(runner).Start(Request());

        Assert.Equal(RunStartStatus.ToolFailure, outcome.Status);
        Assert.Contains(
            outcome.Messages,
            message => message.Kind == RunStartMessageKind.Remedy
                && message.Text.Contains(ExcludeLine, StringComparison.Ordinal)
                && message.Text.Contains(".git/info/exclude", StringComparison.Ordinal));
        Assert.DoesNotContain("WorktreePathFor", runner.Calls);
        Assert.DoesNotContain("CreateBranchInWorktree", runner.Calls);
    }

    [Fact]
    public void AnInPlaceRunNeverWritesTheExclude()
    {
        OrderRunner runner = new(Repository)
        {
            Dirty = false,
            CreateInPlaceResult = new GitOutcome(1, string.Empty, "stop after the branch decision"),
        };

        Service(runner).Start(Request());

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
    }

    private sealed class NoProcesses : IBoundedProcessExecutor
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput, TimeSpan timeout) =>
            throw new NotSupportedException();
    }

    /// <summary>A runner that answers the calls Start makes before the folder step, and records the order of the folder steps.</summary>
    private sealed class OrderRunner(string root) : IRunStartRunner
    {
        public List<string> Calls { get; } = [];

        public bool Dirty { get; init; }

        public GitOutcome ExcludeResult { get; init; } = new(0, string.Empty, string.Empty);

        public GitOutcome CreateInPlaceResult { get; init; } = new(0, string.Empty, string.Empty);

        public string RepositoryRoot => root;

        public GitOutcome Git(params string[] arguments) => new(0, string.Empty, string.Empty);

        public string? DetectRepositoryRoot() => root;

        public GitOutcome CheckoutStatus() => new(0, Dirty ? " M README.md\n" : string.Empty, string.Empty);

        public bool? CheckoutIsClean() => !Dirty;

        public GitOutcome WorktreeList() => new(0, "worktree " + root + "\n", string.Empty);

        public bool? AnotherWorktreeIsAttached() => false;

        public string? OriginHeadSymbolicRef() => null;

        public string? ResolveRevision(string revision) => "abc123";

        public bool HasLocalBranch(string branch) => false;

        public bool HasRemoteBranch(string branch) => false;

        public string? Describe(string revision) => "v1";

        public (int? Ahead, int? Behind) Divergence(string left, string right) => (0, 0);

        public GitOutcome CommitMessages(string baseRef, string tip) => throw new NotSupportedException();

        public string? OriginAddress() => null;

        public bool CodeGraphBinaryIsAvailable() => true;

        public GitOutcome Fetch() => new(0, string.Empty, string.Empty);

        public GitOutcome CreateBranchInPlace(string branch, string startRef)
        {
            Calls.Add("CreateBranchInPlace");
            return CreateInPlaceResult;
        }

        public GitOutcome SwitchToBranch(string branch) => throw new NotSupportedException();

        public string WorktreePathFor(string branch, DateTimeOffset startedAt)
        {
            Calls.Add("WorktreePathFor");
            return Path.Combine(root, ".agent_worktrees", "folder");
        }

        public GitOutcome EnsureWorktreeFolderExcluded()
        {
            Calls.Add("EnsureWorktreeFolderExcluded");
            return ExcludeResult;
        }

        public void ReleaseWorktreeClaim(string worktreePath)
        {
        }

        public GitOutcome CreateBranchInWorktree(string branch, string startRef, string worktreePath)
        {
            Calls.Add("CreateBranchInWorktree");
            return new GitOutcome(1, string.Empty, "stop after the folder decision");
        }

        public GitOutcome AddWorktreeForBranch(string branch, string worktreePath, string? startRef = null) =>
            throw new NotSupportedException();
    }
}
