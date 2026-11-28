using System.Diagnostics;
using System.Text;
using KyberWeave.Application.RunIntegrate;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Processes;
using KyberWeave.Data.Docs.Closeout;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Data.RunStart;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Integrates two real task worktrees into a real run worktree with real git: the first is a fast-forward, the
/// second needs a cherry-pick because the run branch moved after it was cut. Both task worktrees and their
/// branches must be gone and both commits must be on the run branch.
/// </summary>
public sealed class RunIntegrateGitTests : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();
    private string _work = string.Empty;
    private string _run = string.Empty;
    private string _first = string.Empty;
    private string _second = string.Empty;
    private RunIntegrateService _service = null!;

    public async ValueTask InitializeAsync()
    {
        string root = _temp.Path;
        string origin = Path.Combine(root, "origin.git");
        string seed = Path.Combine(root, "seed");
        _work = Path.Combine(root, "work");
        _run = Path.Combine(root, "run");
        _first = Path.Combine(root, "task-one");
        _second = Path.Combine(root, "task-two");

        await GitAt(root, "init", "--bare", "--initial-branch=main", origin);
        await GitAt(root, "clone", origin, seed);
        await ConfigureAuthor(seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "# seed\n", Encoding.UTF8);
        await GitAt(seed, "add", "-A");
        await GitAt(seed, "commit", "-m", "initial");
        await GitAt(seed, "push", "origin", "main");

        await GitAt(root, "clone", origin, _work);
        await ConfigureAuthor(_work);
        await GitAt(_work, "worktree", "add", "-b", "run/main", _run, "main");
        await GitAt(_run, "worktree", "add", "-b", "task/one", _first);
        await GitAt(_run, "worktree", "add", "-b", "task/two", _second);

        IWritableFileSystem files = new PhysicalFileSystem();
        IPathResolver paths = new PhysicalPathResolver(files);
        ProcessEnvironment environment = new();
        IGitCommandRunner git = new ProcessGitCommandRunner(TimeSpan.FromSeconds(60));
        IRunStateStore store = new FileRunStateStore(
            files, new GitCheckedOutBranchReader(git), paths, environment);
        IRunCheckout checkouts = new RunCheckoutGuard(files, store, environment, paths);

        _service = new RunIntegrateService(
            git,
            checkouts,
            files,
            environment,
            paths,
            TimeProvider.System);
    }

    public ValueTask DisposeAsync()
    {
        _temp.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The integration lock is an open handle the operating system owns: a second exclusive open fails while the
    /// first is held, and one succeeds once it is disposed, with no stale state left behind.
    /// </summary>
    [Fact]
    public async Task ASecondExclusiveOpenOfTheLockFileFailsWhileTheFirstIsHeldAndSucceedsAfterItIsDisposed()
    {
        string lockPath = Path.Combine(
            (await GitOutput(_run, "rev-parse", "--absolute-git-dir")).Trim(),
            RunIntegrateService.LockFileName);

        using (FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            _ = Assert.Throws<IOException>(() =>
            {
                using FileStream second = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            });
        }

        using FileStream reopened = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.True(reopened.CanWrite);
    }

    [Fact]
    public async Task TwoTaskWorktreesAreIntegratedAndRemovedOneFastForwardThenOneCherryPick()
    {
        await Commit(_first, "one.txt", "first task\n", "feat: first task");
        Directory.CreateDirectory(Path.Combine(_first, ".agent_scratchpad", "T1"));
        await File.WriteAllTextAsync(
            Path.Combine(_first, ".agent_scratchpad", "T1", "report.md"), "gate passed\n", Encoding.UTF8);
        await Commit(_second, "two.txt", "second task\n", "feat: second task");
        string firstTip = (await GitOutput(_first, "rev-parse", "HEAD")).Trim();
        string secondTip = (await GitOutput(_second, "rev-parse", "HEAD")).Trim();

        RunIntegrateOutcome first = await Integrate("T1", _first);
        string afterFirst = (await GitOutput(_run, "rev-parse", "HEAD")).Trim();

        RunIntegrateOutcome second = await Integrate("T2", _second);
        string afterSecond = (await GitOutput(_run, "rev-parse", "HEAD")).Trim();

        Assert.True(first.Status == RunIntegrateStatus.Integrated, first.Message);
        Assert.True(second.Status == RunIntegrateStatus.Integrated, second.Message);
        Assert.Equal(firstTip, afterFirst);
        Assert.NotEqual(secondTip, afterSecond);

        string log = await GitOutput(_run, "log", "--format=%s", "-2");
        Assert.Contains("feat: first task", log, StringComparison.Ordinal);
        Assert.Contains("feat: second task", log, StringComparison.Ordinal);

        Assert.False(Directory.Exists(_first));
        Assert.False(Directory.Exists(_second));
        Assert.False(await BranchExists("task/one"));
        Assert.False(await BranchExists("task/two"));
        Assert.True(File.Exists(Path.Combine(_run, ".agent_scratchpad", "T1", "report.md")));
    }

    [Fact]
    public async Task AnUnrelatedUncommittedFileInTheRunWorktreeSurvivesAFastForwardUnchanged()
    {
        await Commit(_first, "one.txt", "first task\n", "feat: first task");
        await File.WriteAllTextAsync(
            Path.Combine(_run, "README.md"), "the architect's uncommitted revision\n", Encoding.UTF8);

        RunIntegrateOutcome outcome = await Integrate("T1", _first);

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.Equal(
            "the architect's uncommitted revision\n",
            await File.ReadAllTextAsync(Path.Combine(_run, "README.md"), Encoding.UTF8));
        Assert.Contains(" M README.md", await GitOutput(_run, "status", "--porcelain", "--untracked-files=no"), StringComparison.Ordinal);
        Assert.Contains(outcome.Notes ?? [], note => note.Contains("README.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnrelatedUncommittedFileInTheRunWorktreeSurvivesACherryPickUnchanged()
    {
        await Commit(_second, "two.txt", "second task\n", "feat: second task");
        // The run branch moves after the task branch was cut, so the task needs a cherry-pick.
        await Commit(_run, "run.txt", "run moved on\n", "feat: the run moved on");
        await File.WriteAllTextAsync(
            Path.Combine(_run, "README.md"), "the architect's uncommitted revision\n", Encoding.UTF8);

        RunIntegrateOutcome outcome = await Integrate("T2", _second);

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.Equal(
            "the architect's uncommitted revision\n",
            await File.ReadAllTextAsync(Path.Combine(_run, "README.md"), Encoding.UTF8));
        Assert.Contains("feat: second task", await GitOutput(_run, "log", "--format=%s", "-1"), StringComparison.Ordinal);
        Assert.False(await CherryPickStateExists());
    }

    private async Task<RunIntegrateOutcome> Integrate(string taskId, string taskWorktree) =>
        await _service.IntegrateAsync(new RunIntegrateRequest(taskId, taskWorktree, _run, _work, _work));

    [Fact]
    public async Task ACommitTheRunBranchAlreadyHasUnderAnotherShaIsDroppedAndTheTaskWorktreeRemoved()
    {
        await Commit(_first, "one.txt", "shared change\n", "feat: the task's change");
        // The run branch carries the same change under its own sha, so the task's commit applies empty.
        await Commit(_run, "one.txt", "shared change\n", "feat: the same change, recommitted");
        string runBefore = (await GitOutput(_run, "rev-parse", "HEAD")).Trim();

        RunIntegrateOutcome outcome = await Integrate("T1", _first);

        Assert.True(outcome.Status == RunIntegrateStatus.Integrated, outcome.Message);
        Assert.Equal(runBefore, (await GitOutput(_run, "rev-parse", "HEAD")).Trim());
        Assert.Equal(2, await CountCommits(_run, "run/main"));
        Assert.False(Directory.Exists(_first));
        Assert.False(await BranchExists("task/one"));
        Assert.False(await CherryPickStateExists(), "a dropped commit must leave no cherry-pick state behind");
    }

    [Fact]
    public async Task ACherryPickThatCannotOverwriteAnUntrackedFileIsAbortedAndTheNextIntegrateWorks()
    {
        await Commit(_second, "two.txt", "second task\n", "feat: second task");
        // An untracked file at the path the task's commit adds makes git refuse the pick before any conflict.
        await File.WriteAllTextAsync(
            Path.Combine(_run, "two.txt"), "the operator's untracked file\n", Encoding.UTF8);

        RunIntegrateOutcome blocked = await Integrate("T2", _second);

        Assert.Equal(RunIntegrateStatus.Failed, blocked.Status);
        Assert.False(await CherryPickStateExists(), "a failed cherry-pick must leave no sequencer or CHERRY_PICK_HEAD behind");
        Assert.True(Directory.Exists(_second));

        File.Delete(Path.Combine(_run, "two.txt"));
        RunIntegrateOutcome retried = await Integrate("T2", _second);

        Assert.True(retried.Status == RunIntegrateStatus.Integrated, retried.Message);
        Assert.Contains("feat: second task", await GitOutput(_run, "log", "--format=%s", "-1"), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_second));
    }

    [Fact]
    public async Task TheCanonicalCheckoutIsRefusedAsTheTaskWorktree()
    {
        await Commit(_work, "canonical.txt", "wrong place\n", "feat: work in the canonical checkout");
        string canonicalHead = (await GitOutput(_work, "rev-parse", "HEAD")).Trim();
        string runHead = (await GitOutput(_run, "rev-parse", "HEAD")).Trim();

        RunIntegrateOutcome outcome = await Integrate("T1", _work);

        Assert.Equal(RunIntegrateStatus.RefusedInput, outcome.Status);
        Assert.Contains("canonical checkout", outcome.Message!, StringComparison.Ordinal);
        Assert.Equal(canonicalHead, (await GitOutput(_work, "rev-parse", "HEAD")).Trim());
        Assert.Equal(runHead, (await GitOutput(_run, "rev-parse", "HEAD")).Trim());
    }

    private async Task<bool> CherryPickStateExists()
    {
        string gitDirectory = (await GitOutput(_run, "rev-parse", "--absolute-git-dir")).Trim();
        return File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD"))
            || Directory.Exists(Path.Combine(gitDirectory, "sequencer"));
    }

    private async Task<int> CountCommits(string worktree, string revision) =>
        int.Parse((await GitOutput(worktree, "rev-list", "--count", revision)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
    private async Task Commit(string worktree, string file, string content, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(worktree, file), content, Encoding.UTF8);
        await GitAt(worktree, "add", "-A");
        await GitAt(worktree, "commit", "-m", message);
    }

    private async Task<bool> BranchExists(string branch) =>
        (await RunGit(_work, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch])).ExitCode == 0;

    private async Task<string> GitOutput(string worktree, params string[] arguments) =>
        (await RunGit(worktree, arguments)).StandardOutput;

    private async Task GitAt(string worktree, params string[] arguments)
    {
        ProcessResult result = await RunGit(worktree, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} in '{worktree}' failed with exit {result.ExitCode}: " +
            result.StandardError);
    }

    private static async Task<ProcessResult> RunGit(string worktree, string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = worktree,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60));
    }

    private async Task ConfigureAuthor(string repository)
    {
        await GitAt(repository, "config", "user.name", "Kyber Weave Tests");
        await GitAt(repository, "config", "user.email", "tests@kyber-weave.invalid");
        await GitAt(repository, "config", "commit.gpgsign", "false");
    }
}
