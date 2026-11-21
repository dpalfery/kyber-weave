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
public sealed class RunIntegrateGitTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _work;
    private readonly string _run;
    private readonly string _first;
    private readonly string _second;
    private readonly RunIntegrateService _service;

    public RunIntegrateGitTests()
    {
        string root = _temp.Path;
        string origin = Path.Combine(root, "origin.git");
        string seed = Path.Combine(root, "seed");
        _work = Path.Combine(root, "work");
        _run = Path.Combine(root, "run");
        _first = Path.Combine(root, "task-one");
        _second = Path.Combine(root, "task-two");

        GitAt(root, "init", "--bare", "--initial-branch=main", origin);
        GitAt(root, "clone", origin, seed);
        ConfigureAuthor(seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "# seed\n", Encoding.UTF8);
        GitAt(seed, "add", "-A");
        GitAt(seed, "commit", "-m", "initial");
        GitAt(seed, "push", "origin", "main");

        GitAt(root, "clone", origin, _work);
        ConfigureAuthor(_work);
        GitAt(_work, "worktree", "add", "-b", "run/main", _run, "main");
        GitAt(_run, "worktree", "add", "-b", "task/one", _first);
        GitAt(_run, "worktree", "add", "-b", "task/two", _second);

        IWritableFileSystem files = new PhysicalFileSystem();
        IPathResolver paths = new PhysicalPathResolver(files);
        ProcessEnvironment environment = new();
        IGitCommandRunner git = new ProcessGitCommandRunner(TimeSpan.FromSeconds(60));
        IRunStateStore store = new FileRunStateStore(
            files, new GitCheckedOutBranchReader(git), paths, environment);
        IRunCheckout checkouts = new RunCheckoutGuard(files, store, environment, paths);

        _service = new RunIntegrateService(
            git, checkouts, files, new ExclusiveFileWriter(), environment, paths, TimeProvider.System);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TwoTaskWorktreesAreIntegratedAndRemovedOneFastForwardThenOneCherryPick()
    {
        Commit(_first, "one.txt", "first task\n", "feat: first task");
        Directory.CreateDirectory(Path.Combine(_first, ".agent_scratchpad", "T1"));
        File.WriteAllText(Path.Combine(_first, ".agent_scratchpad", "T1", "report.md"), "gate passed\n", Encoding.UTF8);
        Commit(_second, "two.txt", "second task\n", "feat: second task");
        string firstTip = GitOutput(_first, "rev-parse", "HEAD").Trim();
        string secondTip = GitOutput(_second, "rev-parse", "HEAD").Trim();

        RunIntegrateOutcome first = Integrate("T1", _first);
        string afterFirst = GitOutput(_run, "rev-parse", "HEAD").Trim();

        RunIntegrateOutcome second = Integrate("T2", _second);
        string afterSecond = GitOutput(_run, "rev-parse", "HEAD").Trim();

        Assert.True(first.Status == RunIntegrateStatus.Integrated, first.Message);
        Assert.True(second.Status == RunIntegrateStatus.Integrated, second.Message);
        Assert.Equal(firstTip, afterFirst);
        Assert.NotEqual(secondTip, afterSecond);

        string log = GitOutput(_run, "log", "--format=%s", "-2");
        Assert.Contains("feat: first task", log, StringComparison.Ordinal);
        Assert.Contains("feat: second task", log, StringComparison.Ordinal);

        Assert.False(Directory.Exists(_first));
        Assert.False(Directory.Exists(_second));
        Assert.False(BranchExists("task/one"));
        Assert.False(BranchExists("task/two"));
        Assert.True(File.Exists(Path.Combine(_run, ".agent_scratchpad", "T1", "report.md")));
    }

    private RunIntegrateOutcome Integrate(string taskId, string taskWorktree) =>
        _service.Integrate(new RunIntegrateRequest(taskId, taskWorktree, _run, _work, _work));

    private void Commit(string worktree, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(worktree, file), content, Encoding.UTF8);
        GitAt(worktree, "add", "-A");
        GitAt(worktree, "commit", "-m", message);
    }

    private bool BranchExists(string branch) =>
        RunGit(_work, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch]).ExitCode == 0;

    private string GitOutput(string worktree, params string[] arguments) =>
        RunGit(worktree, arguments).StandardOutput;

    private void GitAt(string worktree, params string[] arguments)
    {
        ProcessResult result = RunGit(worktree, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} in '{worktree}' failed with exit {result.ExitCode}: " +
            result.StandardError);
    }

    private static ProcessResult RunGit(string worktree, string[] arguments)
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
        return ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
    }

    private void ConfigureAuthor(string repository)
    {
        GitAt(repository, "config", "user.name", "Kyber Weave Tests");
        GitAt(repository, "config", "user.email", "tests@kyber-weave.invalid");
        GitAt(repository, "config", "commit.gpgsign", "false");
    }
}
