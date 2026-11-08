using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
///     The decisions <c>kyber-weave run start</c> makes before anyone investigates or plans: which branch
///     to name, whether it may track the base, where the work belongs, and whether a failed fetch stops
///     the run.
/// </summary>
/// <remarks>
///     <para>
///         Every assertion here reads real git state in a real repository — the branch that exists, the
///         upstream it has or has not, and the worktrees attached — rather than a planner type's return
///         value. The planner does not exist yet, and a test that named it would fail to compile, which would
///         make the whole test project unbuildable for every other task in this slice. Driving the command
///         and reading what it did to the repository pins the same decisions through a seam that already
///         exists.
///     </para>
///     <para>
///         The repository is built by <see cref="RunStartProbe" />: a bare origin, a seed commit on
///         <c>main</c>, a <c>develop</c> branch so the base is not assumed, and a working clone whose
///         <c>origin/HEAD</c> is set. Nothing reaches the network.
///     </para>
/// </remarks>
public sealed class RunStartPlannerTests : IDisposable
{
    private readonly RunStartProbe _probe = RunStartProbe.Create();

    public void Dispose()
    {
        _probe.Dispose();
    }

    /// <summary>
    ///     The branch is named from the three arguments an operator supplies, in the shape the pull
    ///     request naming check enforces: <c>&lt;harness&gt;/&lt;type&gt;/&lt;slug&gt;</c>.
    /// </summary>
    /// <remarks>
    ///     The two checks share one vocabulary on purpose. A branch the naming check would reject is a
    ///     branch whose pull request cannot reach the default branch, so creating one is a defect the
    ///     operator only discovers at the end of the work.
    /// </remarks>
    [Fact]
    public void CreatesTheBranchFromTheHarnessTypeAndSlug()
    {
        ProbeResult result = _probe.RunStart("--harness", "commandcode", "--type", "fix", "--slug",
            "arbiter-global-block-identity");

        Assert.True(result.ExitCode == 0, $"run start must exit 0 on a ready run. {result.Output}");
        Assert.Equal("commandcode/fix/arbiter-global-block-identity", _probe.CurrentBranch());
    }

    /// <summary>
    ///     The new branch does not track the remote base, because a fix branch that tracks
    ///     <c>origin/main</c> lets a bare <c>git push</c> aim at the wrong branch.
    /// </summary>
    [Fact]
    public void CreatesTheBranchWithoutAnUpstreamSoABarePushCannotAimAtTheBase()
    {
        const string Branch = "commandcode/fix/arbiter-global-block-identity";

        ProbeResult result = _probe.RunStart("--harness", "commandcode", "--type", "fix", "--slug",
            "arbiter-global-block-identity");
        Assert.True(result.ExitCode == 0, $"run start must exit 0 on a ready run. {result.Output}");

        string upstream = _probe.GitOutput("for-each-ref", "--format=%(upstream)", "refs/heads/" + Branch).Trim();

        Assert.True(
            upstream.Length == 0,
            $"'{Branch}' must be created with --no-track, but it tracks '{upstream}'. A branch that " +
            "tracks the remote base lets a bare 'git push' aim at it.");
    }

    /// <summary>
    ///     A clean checkout nobody else is using is branched where it stands, so the operator keeps
    ///     working in the directory they are already in.
    /// </summary>
    [Fact]
    public void BranchesACleanUnusedCheckoutInPlace()
    {
        int worktreesBefore = _probe.WorktreeCount();

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 0, $"run start must exit 0 on a ready run. {result.Output}");
        Assert.Equal("claude/fix/tighten-the-gate", _probe.CurrentBranch());
        Assert.True(
            _probe.WorktreeCount() == worktreesBefore,
            $"A clean unused checkout must be branched in place, but the worktree count went from " +
            $"{worktreesBefore} to {_probe.WorktreeCount()}.");
    }

    /// <summary>
    ///     A checkout with uncommitted changes is left exactly as the operator left it, and the new
    ///     branch is created in its own worktree instead.
    /// </summary>
    /// <remarks>
    ///     Switching branches under uncommitted work either fails or carries the edits onto a branch they
    ///     were not made for. A worktree leaves both the edits and their branch untouched.
    /// </remarks>
    [Fact]
    public void BranchesADirtyCheckoutInAWorktreeInsteadOfInPlace()
    {
        const string Branch = "claude/fix/tighten-the-gate";
        _probe.AppendLine("README.md", "uncommitted work");
        string currentBefore = _probe.CurrentBranch();
        int worktreesBefore = _probe.WorktreeCount();

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 0, $"run start must exit 0 on a dirty checkout. {result.Output}");
        Assert.Equal(currentBefore, _probe.CurrentBranch());
        Assert.True(
            _probe.WorktreeCount() == worktreesBefore + 1,
            $"A dirty checkout must get its own worktree, but the worktree count went from " +
            $"{worktreesBefore} to {_probe.WorktreeCount()}.");
        Assert.Contains(
            Branch,
            _probe.WorktreeBranches(),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     A checkout another worktree is already using is treated like a dirty one: the new branch goes
    ///     to its own worktree rather than taking over the checkout in use.
    /// </summary>
    [Fact]
    public void BranchesABusyCheckoutInAWorktreeInsteadOfInPlace()
    {
        const string Branch = "claude/fix/tighten-the-gate";
        string currentBefore = _probe.CurrentBranch();
        _probe.AddLinkedWorktree("spare");
        int worktreesBefore = _probe.WorktreeCount();

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 0, $"run start must exit 0 on a busy checkout. {result.Output}");
        Assert.Equal(currentBefore, _probe.CurrentBranch());
        Assert.True(
            _probe.WorktreeCount() == worktreesBefore + 1,
            $"A checkout already in use must get the new branch in its own worktree, but the worktree " +
            $"count went from {worktreesBefore} to {_probe.WorktreeCount()}.");
        Assert.Contains(Branch, _probe.WorktreeBranches(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A resumed branch is where the work continues: the command checks it out and creates nothing
    ///     new, because the plan or pull request already owns that branch and its history.
    /// </summary>
    [Fact]
    public void ResumesTheNamedBranchInsteadOfCreatingANewOne()
    {
        const string Resumed = "claude/fix/existing-fix";
        _probe.CreateBranchWithCommit(Resumed, "earlier work");
        _probe.Git("switch", "main");

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--resume", Resumed);

        Assert.True(result.ExitCode == 0, $"run start must exit 0 when resuming. {result.Output}");
        Assert.Equal(Resumed, _probe.CurrentBranch());
        Assert.False(
            _probe.BranchExists("claude/fix/tighten-the-gate"),
            "A resume must keep the resumed branch rather than creating a second one from the arguments.");
    }

    /// <summary>
    ///     A resumed branch passed as <c>origin/&lt;branch&gt;</c> is accepted when the branch exists on the
    ///     remote, checking out that branch without duplicating the remote prefix into the branch name.
    /// </summary>
    [Fact]
    public void ResumesRemoteBranchWhenPassedWithOriginPrefix()
    {
        const string Resumed = "claude/fix/remote-fix";
        _probe.CreateBranchWithCommit(Resumed, "earlier work");
        _probe.Git("push", "origin", Resumed);
        _probe.Git("switch", "main");
        _probe.Git("branch", "-D", Resumed);

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--resume", "origin/" + Resumed);

        Assert.True(result.ExitCode == 0, $"run start must exit 0 when resuming origin/branch. {result.Output}");
        Assert.Equal(Resumed, _probe.CurrentBranch());
    }

    /// <summary>
    ///     When the user explicitly passes <c>--resume origin/&lt;branch&gt;</c>, the remote branch is
    ///     preferred even if a stale local branch of the same name exists, comparing divergence against
    ///     the remote revision.
    /// </summary>
    [Fact]
    public void PrefersRemoteBranchWhenExplicitlyQualifiedWithOriginPrefix()
    {
        const string Resumed = "claude/fix/diverged-fix";
        _probe.CreateBranchWithCommit(Resumed, "local stale commit");
        string localRevision = _probe.GitOutput("rev-parse", "HEAD").Trim();

        _probe.Git("switch", "main");
        _probe.CreateBranchWithCommit("temp-remote", "remote commit");
        _probe.Git("push", "origin", "temp-remote:" + Resumed);
        string remoteRevision = _probe.GitOutput("rev-parse", "origin/" + Resumed).Trim();
        _probe.Git("switch", "main");
        _probe.Git("branch", "-D", "temp-remote");

        Assert.NotEqual(localRevision, remoteRevision);

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--resume", "origin/" + Resumed,
            "--json");

        Assert.True(result.ExitCode == 0, $"run start must exit 0: {result.Output}");
        Assert.Contains(remoteRevision, result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    ///     When resuming a branch that exists only on the remote while another worktree is attached,
    ///     the command creates a worktree with a tracking branch from the remote ref.
    /// </summary>
    [Fact]
    public void ResumesRemoteOnlyBranchInWorktreeWithTrackingBranch()
    {
        const string Resumed = "claude/fix/remote-worktree-tracking";
        _probe.CreateBranchWithCommit(Resumed, "remote work");
        _probe.Git("push", "origin", Resumed);
        _probe.Git("switch", "main");
        _probe.Git("branch", "-D", Resumed);

        _probe.AddLinkedWorktree("linked-worker");

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--resume", "origin/" + Resumed);

        Assert.True(result.ExitCode == 0, $"run start must exit 0 when resuming remote branch in worktree: {result.Output}");
        Assert.Contains(Resumed, _probe.WorktreeBranches(), StringComparison.Ordinal);
        Assert.True(_probe.BranchExists(Resumed), $"Local branch '{Resumed}' must be created.");
    }

    /// <summary>
    ///     Refuses --resume HEAD or --resume origin/HEAD with exit 2 because HEAD is a symbolic reference,
    ///     not a branch.
    /// </summary>
    [Theory]
    [InlineData("HEAD")]
    [InlineData("origin/HEAD")]
    public void RefusesToResumeHeadSymbolicReferenceWithExitTwo(string candidate)
    {
        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--resume", candidate);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("symbolic reference", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     When resuming a branch that exists only on the remote in a dirty checkout, the command creates
    ///     the worktree for the tracking branch without failing on an invalid reference.
    /// </summary>
    [Fact]
    public void ResumesRemoteOnlyBranchInWorktreeWhenCheckoutIsDirty()
    {
        const string Resumed = "claude/fix/remote-only-worktree";
        _probe.CreateBranchWithCommit(Resumed, "remote work");
        _probe.Git("push", "origin", Resumed);
        _probe.Git("switch", "main");
        _probe.Git("branch", "-D", Resumed);
        _probe.Git("config", "worktree.guessRemote", "false");

        _probe.AppendLine("README.md", "dirty local changes");

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate",
            "--resume", "origin/" + Resumed);

        Assert.True(result.ExitCode == 0, $"run start must exit 0 when resuming in worktree: {result.Output}");
        Assert.Contains(Resumed, _probe.WorktreeBranches(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     When the base is origin/HEAD and neither origin/HEAD nor an origin default branch can be resolved,
    ///     the command fails rather than guessing 'main'.
    /// </summary>
    [Fact]
    public void FailsWhenOriginHeadCannotBeResolvedInsteadOfGuessingMain()
    {
        _probe.GitRemote("symbolic-ref", "HEAD", "refs/heads/nonexistent");
        _probe.Git("remote", "set-head", "origin", "-d");

        ProbeResult result = _probe.RunStart(
            "--harness", "claude",
            "--type", "fix",
            "--slug", "tighten-the-gate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no ref for the base origin/HEAD resolves", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A fetch that fails stops the run when <c>stop-on-tool-failure</c> is unset, because absence is
    ///     the stop setting and a run that continues from a base it could not refresh plans against stale
    ///     history.
    /// </summary>
    [Fact]
    public void StopsOnAFetchFailureWhenStopOnToolFailureIsUnset()
    {
        _probe.BreakOrigin();

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(
            result.ExitCode == 1,
            $"A failed fetch with stop-on-tool-failure unset must exit 1, not {result.ExitCode}. {result.Output}");
        Assert.False(
            _probe.BranchExists("claude/fix/tighten-the-gate"),
            "A stopped run must not leave a branch behind for the operator to discover later.");
    }

    /// <summary>
    ///     The same failed fetch continues when the operator turned <c>stop-on-tool-failure</c> off, so a
    ///     repository that accepts a stale base still gets its branch.
    /// </summary>
    [Fact]
    public void ContinuesPastAFetchFailureWhenStopOnToolFailureIsFalse()
    {
        _probe.WriteHostConfig("stop-on-tool-failure: false\n");
        _probe.BreakOrigin();

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(
            result.ExitCode == 0,
            $"stop-on-tool-failure: false must let the run continue past a failed fetch, " +
            $"but it exited {result.ExitCode}. {result.Output}");
        Assert.True(
            _probe.BranchExists("claude/fix/tighten-the-gate"),
            "A run that continued past the fetch must still create its branch.");
    }

    /// <summary>
    ///     The base is the branch the run was told to compare against, not a hard-coded <c>main</c>: the
    ///     new branch starts at the configured base's tip.
    /// </summary>
    [Fact]
    public void BranchesFromTheConfiguredBaseBranchRatherThanAssumingMain()
    {
        const string Branch = "claude/fix/tighten-the-gate";
        _probe.WriteHostConfig("run:\n  base-branch: develop\n");
        string developTip = _probe.GitOutput("rev-parse", "origin/develop").Trim();

        ProbeResult result = _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");

        Assert.True(result.ExitCode == 0, $"run start must exit 0 on a ready run. {result.Output}");
        Assert.True(
            string.Equals(_probe.GitOutput("rev-parse", "refs/heads/" + Branch).Trim(), developTip,
                StringComparison.Ordinal),
            $"'{Branch}' must start at origin/develop ({developTip}), not at main. The configured " +
            "run.base-branch is the base, so a repository whose default is not main is compared " +
            "against the branch its operator named.");
    }
}

/// <summary>
///     A real git repository with a real <c>origin</c>, driven by the real CLI, for the run-start
///     suites.
/// </summary>
/// <remarks>
///     <para>
///         It lives in this file because the dispatch that authored these tests may create only the three
///         run-start test files; a shared fixture under <c>Fakes/</c> would have been the better home.
///     </para>
///     <para>
///         It is also where both suites' child processes live, so
///         <c>Fixtures/spawn-sites-allowlist.json</c> bounds three sites instead of six: one for the CLI,
///         one for git, and the <see cref="ProcessRunner.Run" /> call they share.
///     </para>
/// </remarks>
internal sealed class RunStartProbe : IDisposable
{
    private const string CliHostName = "kyber-weave";

    /// <summary>
    ///     A stand-in for the CodeGraph binary. The run-start step reports exit 3 when it is genuinely
    ///     missing, so a suite that exercised that path everywhere would fail on a machine without it and
    ///     pass on one with it; the stub makes every other case independent of that.
    /// </summary>
    private const string CodegraphStub = "#!/bin/sh\necho 'codegraph 0.0.0-stub'\nexit 0\n";

    private readonly string _home;
    private readonly string _pathWithStub;
    private readonly string _pathWithoutCodegraph;
    private readonly string _stubBin;

    private readonly TempDirectory _temp = new();
    private readonly string _tmp;

    private RunStartProbe()
    {
        _home = Path.Combine(_temp.Path, "home");
        _tmp = Path.Combine(_temp.Path, "tmp");
        _stubBin = Path.Combine(_temp.Path, "bin");
        Remote = Path.Combine(_temp.Path, "origin.git");
        Root = Path.Combine(_temp.Path, "work");

        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_tmp);
        Directory.CreateDirectory(_stubBin);

        string inherited = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        _pathWithStub = _stubBin + Path.PathSeparator + inherited;

        // git and nothing that answers to the CodeGraph name, so the missing-binary case is a fact of
        // this PATH rather than a fact of the machine. The stub directory is deliberately left out:
        // it holds the codegraph the other cases rely on.
        string? gitDirectory = Path.GetDirectoryName(ResolveOnPath("git", inherited));
        _pathWithoutCodegraph = gitDirectory ?? inherited;
    }

    /// <summary>The working clone the operator would be standing in.</summary>
    public string Root { get; }

    /// <summary>The bare repository the clone calls <c>origin</c>.</summary>
    public string Remote { get; }

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    ///     Builds the origin and the working clone: <c>main</c> and <c>develop</c> on the remote, and a
    ///     clone whose <c>origin/HEAD</c> is set so the default base resolves.
    /// </summary>
    public static RunStartProbe Create()
    {
        SkipIfGitUnavailable();

        RunStartProbe probe = new();
        probe.WriteCodegraphStub();
        probe.InitializeRepository();
        return probe;
    }

    /// <summary>Runs <c>kyber-weave run start</c> in the working clone with the CodeGraph stub on PATH.</summary>
    public ProbeResult RunStart(params string[] arguments)
    {
        return RunStartOn(_pathWithStub, arguments);
    }

    /// <summary>
    ///     Runs <c>kyber-weave run start</c> with a PATH that answers to git but not to
    ///     <c>codegraph</c>.
    /// </summary>
    public ProbeResult RunStartWithoutCodegraph(params string[] arguments)
    {
        return RunStartOn(_pathWithoutCodegraph, arguments);
    }

    /// <summary>Whether <c>codegraph</c> still resolves on the PATH the other method builds.</summary>
    public bool CodegraphResolvesWithoutItsStub()
    {
        return ResolveOnPath("codegraph", _pathWithoutCodegraph) is not null;
    }

    /// <summary>Runs any other CLI verb, for the help and usage cases.</summary>
    public ProbeResult RunCli(params string[] arguments)
    {
        List<string> argv = [.. arguments];
        return RunCliWith(_pathWithStub, argv);
    }

    /// <summary>Runs git in the working clone and asserts it succeeded.</summary>
    public void Git(params string[] arguments)
    {
        ProbeResult result = RunGit(Root, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: {result.Output}");
    }

    /// <summary>Runs git on the bare origin repository.</summary>
    public void GitRemote(params string[] arguments)
    {
        GitAt(Remote, arguments);
    }

    /// <summary>Runs git in the working clone and returns its standard output, asserting success.</summary>
    public string GitOutput(params string[] arguments)
    {
        ProbeResult result = RunGit(Root, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: {result.Output}");
        return result.StandardOutput;
    }

    /// <summary>The branch the working clone currently has checked out.</summary>
    public string CurrentBranch()
    {
        return GitOutput("branch", "--show-current").Trim();
    }

    /// <summary>Whether a local branch of that name exists, checked out anywhere or not.</summary>
    public bool BranchExists(string branch)
    {
        return RunGit(Root, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch]).ExitCode == 0;
    }

    /// <summary>How many worktrees are attached to the working clone, the main one included.</summary>
    public int WorktreeCount()
    {
        return WorktreePaths().Count;
    }

    /// <summary>The branch each attached worktree has checked out, in <c>git worktree list</c> order.</summary>
    public string WorktreeBranches()
    {
        return string.Join(
            "\n",
            GitOutput("worktree", "list", "--porcelain")
                .Split('\n')
                .Where(line => line.StartsWith("branch ", StringComparison.Ordinal))
                .Select(line => line["branch refs/heads/".Length..].Trim()));
    }

    /// <summary>Appends a line to a tracked file, so the checkout is dirty without being broken.</summary>
    public void AppendLine(string relativePath, string line)
    {
        string path = Path.Combine(Root, relativePath);
        File.AppendAllText(path, line + "\n", Encoding.UTF8);
    }

    /// <summary>
    ///     Attaches a second worktree on its own branch, so the main checkout is one another worktree is
    ///     already sharing the repository with.
    /// </summary>
    public void AddLinkedWorktree(string branch)
    {
        Git("branch", branch, "main");
        Git("worktree", "add", Path.Combine(_temp.Path, "linked-" + branch), branch);
    }

    /// <summary>Creates a branch with one commit of its own, so resuming it is visibly different work.</summary>
    public void CreateBranchWithCommit(string branch, string message)
    {
        Git("switch", "-c", branch, "origin/main");
        File.WriteAllText(Path.Combine(Root, "earlier.txt"), message + "\n", Encoding.UTF8);
        Git("add", "-A");
        Git("commit", "-m", message);
    }

    /// <summary>Writes the host configuration a run reads its settings from.</summary>
    public void WriteHostConfig(string yaml)
    {
        string directory = Path.Combine(Root, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), yaml, Encoding.UTF8);
    }

    /// <summary>Points <c>origin</c> at nothing, so the next fetch fails without touching the network.</summary>
    public void BreakOrigin()
    {
        Git("remote", "set-url", "origin", Path.Combine(_temp.Path, "absent.git"));
    }

    private ProbeResult RunStartOn(string path, params string[] arguments)
    {
        List<string> argv = ["run", "start", .. arguments];
        return RunCliWith(path, argv);
    }

    private ProbeResult RunCliWith(string path, List<string> argv)
    {
        ProcessStartInfo startInfo = new(CliHostPath())
        {
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in argv) startInfo.ArgumentList.Add(argument);

        Isolate(startInfo);
        startInfo.Environment["PATH"] = path;
        return Run(startInfo);
    }

    private ProbeResult RunGit(string workingDirectory, string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        Isolate(startInfo);
        return Run(startInfo);
    }

    /// <summary>
    ///     Bounds every child at sixty seconds, killing its tree on expiry, and is the one place this
    ///     suite starts a process.
    /// </summary>
    private static ProbeResult Run(ProcessStartInfo startInfo)
    {
        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        return new ProbeResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardOutput + "\n" + result.StandardError);
    }

    /// <summary>
    ///     Keeps a child off this machine's per-user state: its own HOME, temp directory and XDG root.
    ///     Set on the child's own environment, never through the process's.
    /// </summary>
    private void Isolate(ProcessStartInfo startInfo)
    {
        string configHome = Path.Combine(_home, ".config");
        Directory.CreateDirectory(configHome);

        startInfo.Environment["HOME"] = _home;
        startInfo.Environment["USERPROFILE"] = _home;
        startInfo.Environment["XDG_CONFIG_HOME"] = configHome;
        startInfo.Environment["TMPDIR"] = _tmp;
        startInfo.Environment["TEMP"] = _tmp;
        startInfo.Environment["TMP"] = _tmp;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    private void InitializeRepository()
    {
        GitAt(_temp.Path, "init", "--bare", "--initial-branch=main", Remote);

        string seed = Path.Combine(_temp.Path, "seed");
        GitAt(_temp.Path, "clone", Remote, seed);
        ConfigureAuthor(seed);

        File.WriteAllText(Path.Combine(seed, "README.md"), "# seed\n", Encoding.UTF8);
        GitAt(seed, "add", "-A");
        GitAt(seed, "commit", "-m", "initial");
        GitAt(seed, "push", "origin", "main");

        // A second branch on the remote, so "the base is the configured branch" is separable from
        // "the base is whatever main happens to be".
        GitAt(seed, "switch", "-c", "develop");
        File.WriteAllText(Path.Combine(seed, "develop.txt"), "develop\n", Encoding.UTF8);
        GitAt(seed, "add", "-A");
        GitAt(seed, "commit", "-m", "develop");
        GitAt(seed, "push", "origin", "develop");

        GitAt(_temp.Path, "clone", Remote, Root);
        ConfigureAuthor(Root);
        Git("remote", "set-head", "origin", "--auto");
    }

    private void ConfigureAuthor(string repository)
    {
        GitAt(repository, "config", "user.name", "Kyber Weave Tests");
        GitAt(repository, "config", "user.email", "tests@kyber-weave.invalid");
        GitAt(repository, "config", "commit.gpgsign", "false");
    }

    private void GitAt(string workingDirectory, params string[] arguments)
    {
        ProbeResult result = RunGit(workingDirectory, arguments);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} in '{workingDirectory}' failed with exit {result.ExitCode}: {result.Output}");
    }

    private List<string> WorktreePaths()
    {
        return GitOutput("worktree", "list", "--porcelain")
            .Split('\n')
            .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(line => line["worktree ".Length..].Trim())
            .ToList();
    }

    private void WriteCodegraphStub()
    {
        string path = Path.Combine(_stubBin, "codegraph");
        File.WriteAllText(path, CodegraphStub, Encoding.UTF8);

        // The suite skips on Windows, so this only runs where the mode bits exist.
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>
    ///     Writes a wrapper for git into PATH that fails whenever <paramref name="subcommand" /> is called.
    /// </summary>
    public void WriteGitFailOn(string subcommand, string errorMessage)
    {
        string gitPath = ResolveOnPath("git", _pathWithoutCodegraph) ?? "/usr/bin/git";
        string script =
            "#!/bin/sh\n" +
            "for arg in \"$@\"; do\n" +
            $"  if [ \"$arg\" = \"{subcommand}\" ]; then\n" +
            $"    echo \"{errorMessage}\" >&2\n" +
            "    exit 128\n" +
            "  fi\n" +
            "done\n" +
            $"exec \"{gitPath}\" \"$@\"\n";

        string path = Path.Combine(_stubBin, OperatingSystem.IsWindows() ? "git.cmd" : "git");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(script));

        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string CliHostPath()
    {
        string hostName = OperatingSystem.IsWindows() ? CliHostName + ".exe" : CliHostName;
        string host = Path.Combine(AppContext.BaseDirectory, hostName);
        Assert.True(
            File.Exists(host),
            $"The kyber-weave host '{host}' was not found in AppContext.BaseDirectory.");
        return host;
    }

    /// <summary>The first directory on <paramref name="path" /> holding an executable of that name.</summary>
    private static string? ResolveOnPath(string name, string path)
    {
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static void SkipIfGitUnavailable()
    {
        if (OperatingSystem.IsWindows())
            throw SkipException.ForSkip("These cases build a real git repository and need a POSIX shell.");

        if (ResolveOnPath("git", Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin") is null)
            throw SkipException.ForSkip("These cases need git on PATH to build a real repository.");
    }
}

/// <summary>What one child process wrote, and how it ended.</summary>
/// <param name="ExitCode">The exit code the child returned.</param>
/// <param name="StandardOutput">Its standard output on its own, for the JSON cases.</param>
/// <param name="Output">Both streams joined, for the cases that assert on a message.</param>
internal sealed record ProbeResult(int ExitCode, string StandardOutput, string Output);
