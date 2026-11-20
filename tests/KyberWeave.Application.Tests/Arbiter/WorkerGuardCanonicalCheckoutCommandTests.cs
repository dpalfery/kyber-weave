using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;
using KyberWeave.Data.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-CHECKOUT-001</c> also covers a shell call: while a live run works in a worktree of its own, the
/// worker guard denies a command that would run in the repository's canonical checkout, either from a working
/// directory inside it or after a <c>cd</c>, <c>pushd</c> or <c>git -C</c> into it. The checkout guard, the run
/// records and the git files are the real ones over an in-memory file system, so this is the same decision the
/// hook makes with no repository on disk.
/// </summary>
public sealed class WorkerGuardCanonicalCheckoutCommandTests
{
    private const string RuleId = "KW-ARB-CHECKOUT-001";
    private const string Branch = "copilot/feat/add-widget";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeProcessEnvironment _environment;
    private readonly string _home;
    private readonly string _repo;
    private readonly string _worktree;
    private readonly RunCheckoutGuard _checkout;

    public WorkerGuardCanonicalCheckoutCommandTests()
    {
        _home = Path.Combine(_files.Root, "kw", "home");
        _repo = Path.Combine(_files.Root, "kw", "repo");
        _worktree = Path.Combine(_files.Root, "kw", "repo.worktrees", "add-widget");
        _files.CreateDirectory(_home);
        _files.CreateDirectory(Path.Combine(_repo, ".git"));
        _files.WriteAllText(Path.Combine(_repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        _files.CreateDirectory(Path.Combine(_worktree, "src"));
        _files.WriteAllText(
            Path.Combine(_worktree, ".git"),
            $"gitdir: {Path.Combine(_repo, ".git", "worktrees", "add-widget")}\n");
        _files.CreateDirectory(Path.Combine(_repo, ".git", "worktrees", "add-widget"));
        _files.WriteAllText(Path.Combine(_repo, ".git", "worktrees", "add-widget", "HEAD"), $"ref: refs/heads/{Branch}\n");

        _environment = new FakeProcessEnvironment(
            currentDirectory: _worktree,
            homeDirectory: _home,
            isFileSystemCaseInsensitive: !_files.CaseSensitive);
        _checkout = new RunCheckoutGuard(
            _files,
            new FileRunStateStore(_files, new UnusedBranches(), new FakePathResolver(_files), _environment),
            _environment,
            new FakePathResolver(_files));
    }

    [Fact]
    public void ACommandFromTheCanonicalCheckoutIsRefusedWhileARunIsLive()
    {
        StartRun();

        string reason = DenialReason(Guard("copilot-cli", Copilot(_repo, "dotnet test")));

        Assert.Equal(
            $"{RuleId}: this command would run in the repository's canonical checkout {_repo} while your run works " +
            $"in {_worktree}. Start the command with `cd {_worktree} && ` so every step runs in the run worktree; " +
            "the canonical checkout stays untouched for the whole run.",
            reason);
    }

    [Fact]
    public void AChangeDirectoryIntoTheCanonicalCheckoutIsRefusedFromTheRunWorktree()
    {
        StartRun();

        string reason = DenialReason(Guard("copilot-cli", Copilot(_worktree, $"cd {_repo} && git cherry-pick abc123")));

        Assert.StartsWith(RuleId, reason, StringComparison.Ordinal);
        Assert.Contains(_repo, reason, StringComparison.Ordinal);
        Assert.Contains(_worktree, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AChangeDirectoryAfterAJoinIsRefusedToo()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_worktree, $"dotnet test && cd {_repo} && git commit -am x")));
    }

    [Fact]
    public void AChangeDirectoryInsideASubstitutionIsRefused()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_worktree, $"echo $(cd {_repo} && git rev-parse HEAD)")));
    }

    [Fact]
    public void AQuotedWordThatReadsAsChangeDirectoryIsNotACommand()
    {
        StartRun();

        Assert.Empty(Guard("copilot-cli", Copilot(_worktree, $"grep -rn \"pushd\" {_repo}")));
    }

    [Fact]
    public void AGitDirectoryOptionIntoTheCanonicalCheckoutIsRefused()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_worktree, $"git -C {_repo} status")));
    }

    [Fact]
    public void AChangeDirectoryIntoTheRunWorktreeIsAllowed()
    {
        StartRun();

        Assert.Empty(Guard("copilot-cli", Copilot(_worktree, $"cd {_worktree} && dotnet test")));
    }

    [Fact]
    public void ACommandThatStepsIntoTheRunWorktreeFromTheCanonicalCheckoutIsAllowed()
    {
        StartRun();

        Assert.Empty(Guard("copilot-cli", Copilot(_repo, $"cd {_worktree} && kyber-weave docs drift .")));
    }

    [Fact]
    public void AQuotedChangeDirectoryIntoTheRunWorktreeFromTheCanonicalCheckoutIsAllowed()
    {
        StartRun();

        Assert.Empty(Guard(
            "copilot-cli",
            Copilot(_repo, $"cd '{_worktree}' && kyber-weave docs tasks x --write && kyber-weave docs tasks x --check")));
    }

    [Fact]
    public void ACommandThatStaysInTheCanonicalCheckoutIsStillRefused()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_repo, "kyber-weave docs drift .")));
    }

    [Fact]
    public void ACommandThatStepsBackIntoTheCanonicalCheckoutIsRefused()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_repo, $"cd {_worktree} && cd {_repo} && ls")));
    }

    [Fact]
    public void ACommandWhoseFirstStepIsNotAChangeDirectoryIsStillJudgedOnTheCanonicalCheckout()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_repo, $"ls && cd {_worktree}")));
    }

    [Fact]
    public void EveryStepNamingItsOwnGitDirectoryFromTheCanonicalCheckoutIsAllowed()
    {
        StartRun();

        Assert.Empty(Guard(
            "copilot-cli",
            Copilot(_repo, $"git -C {_worktree} status -sb && git -C {_worktree} worktree list")));
    }

    [Fact]
    public void AGitDirectoryStepFollowedByAChangeDirectoryIntoTheRunWorktreeIsAllowed()
    {
        StartRun();

        Assert.Empty(Guard(
            "copilot-cli",
            Copilot(_repo, $"git -C {_worktree} status -sb && cd {_worktree} && ls")));
    }

    [Fact]
    public void AGitDirectoryStepFollowedByAPlainGitStepIsStillRefused()
    {
        StartRun();

        Assert.NotEmpty(Guard(
            "copilot-cli",
            Copilot(_repo, $"git -C {_worktree} status -sb && git worktree list")));
    }

    [Fact]
    public void AGitDirectoryStepIntoTheCanonicalCheckoutIsStillRefused()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_repo, $"git -C {_repo} status")));
    }

    [Fact]
    public void APlainGitStepFromTheCanonicalCheckoutIsStillRefused()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_repo, "git status")));
    }

    [Fact]
    public void WithNoLiveRunNothingChanges()
    {
        Assert.Empty(Guard("copilot-cli", Copilot(_repo, $"cd {_repo} && git cherry-pick abc123")));
        Assert.Empty(Guard("copilot-cli", Copilot(_worktree, $"git -C {_repo} status")));
    }

    [Fact]
    public void AViewOfTheCanonicalCheckoutStaysAllowed()
    {
        StartRun();

        Assert.Empty(Guard("copilot-cli", CopilotView(_repo)));
    }

    [Fact]
    public void ClaudeBashFromTheCanonicalCheckoutIsRefused()
    {
        StartRun();
        string json = JsonSerializer.Serialize(new
        {
            cwd = _repo,
            hook_event_name = "PreToolUse",
            agent_id = "agent-7",
            agent_type = "architect",
            tool_name = "Bash",
            tool_input = new { command = "dotnet build" },
        });

        using JsonDocument document = JsonDocument.Parse(Guard("claude", json));

        Assert.Equal(
            "deny",
            document.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void CopilotPowerShellFromTheCanonicalCheckoutIsRefused()
    {
        StartRun();

        Assert.NotEmpty(Guard("copilot-cli", Copilot(_repo, "dotnet build", tool: "powershell")));
    }

    /// <summary>
    /// The commands this guard judges, each with the call's working directory (null for the canonical checkout),
    /// whether the guard allows it, and whether the retry prefix the refusal names makes it allowed. <c>{wt}</c>
    /// stands for the run worktree, and a newline is a real newline in the command.
    /// </summary>
    public static TheoryData<string, string?, bool, bool> CommandsJudgedByTheirStepDirectories => new()
    {
        // A subshell's cd ends at the closing `)`, so a later step runs in the outer shell's directory.
        { "(cd {wt}) && rm -rf src", null, false, true },
        { "(cd {wt} && ls)", null, true, false },
        { "(cd {wt} && ls) && ls", null, false, true },

        // A step after `||` runs only when the change directory failed, so it may still run in the cwd.
        { "cd {wt} || rm -rf src", null, false, true },

        // A relative path resolves against the call's working directory.
        { "cd ../repo.worktrees/add-widget && ls", null, true, false },
        { "cd ../../repo && ls", "{wt}", false, false },

        // pushd, and the forms the guard cannot place.
        { "pushd {wt} && ls", null, true, false },
        { "pushd && ls", null, false, false },
        { "cd - && ls", null, false, false },
        { "cd && ls", null, false, false },
        { "cd $WT && ls", null, false, false },
        { "cd \"${WT}\" && ls", null, false, false },

        // A quoted or trailing-separator path names the same directory.
        { "cd {wt}/ && ls", null, true, false },
        { "cd '{wt}' && ls", null, true, false },

        // git's own -C is a directory; a subcommand's -C is not.
        { "git -C{wt} status", null, true, false },
        { "git -C={wt} status", null, true, false },
        { "git -c core.x=y -C {wt} status", null, true, false },
        { "git commit -C HEAD", null, false, true },

        // Separators: a join carries the shell, a pipeline stage does not.
        { "cd {wt}; ls", null, true, false },
        { "cd {wt} | ls", null, false, true },
        { "cd {wt}\nls", null, true, false },
        { "ls; cd {wt}", null, false, true },

        // A command the guard cannot read is refused rather than guessed at.
        { "bash -c \"cd {wt} && ls\"", null, false, true },

        // The plain refusal whose retry prefix must make it allowed.
        { "rm -rf src", null, false, true },
    };

    [Theory]
    [MemberData(nameof(CommandsJudgedByTheirStepDirectories))]
    public void EveryStepIsJudgedByTheDirectoryItRunsIn(string command, string? cwd, bool allowed, bool retryPrefixSuffices)
    {
        StartRun();
        string resolved = command.Replace("{wt}", _worktree, StringComparison.Ordinal);
        string callCwd = (cwd ?? _repo).Replace("{wt}", _worktree, StringComparison.Ordinal);

        string answer = Guard("copilot-cli", Copilot(callCwd, resolved));

        if (allowed)
        {
            Assert.Empty(answer);
            return;
        }

        string reason = DenialReason(answer);
        Assert.StartsWith(RuleId, reason, StringComparison.Ordinal);
        if (retryPrefixSuffices)
        {
            Assert.Empty(Guard("copilot-cli", Copilot(callCwd, RetryPrefix(reason) + resolved)));
        }
    }

    private static string RetryPrefix(string reason)
    {
        const string Marker = "Start the command with `";
        int start = reason.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The refusal does not name the retry prefix: {reason}");
        start += Marker.Length;
        int end = reason.IndexOf('`', start);
        Assert.True(end > start, $"The refusal does not close the retry prefix: {reason}");
        return reason[start..end];
    }

    private void StartRun() =>
        new FileRunStateStore(_files, new UnusedBranches(), new FakePathResolver(_files), _environment).Write(
            _home,
            Branch,
            "origin/main",
            new RunStartPlace(Path.Combine(_repo, ".git"), _worktree, DateTimeOffset.UnixEpoch));

    private string Guard(string harness, string json)
    {
        WorkerGuard guard = new(
            new NoBudgets(),
            _checkout,
            new NoConfig(),
            _environment,
            TimeProvider.System,
            new NoState(),
            _files);
        using StringWriter stdout = new();
        guard.Run(harness, json, stdout, TextWriter.Null, _home);
        return stdout.ToString();
    }

    private static string DenialReason(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("permissionDecision").GetString());
        return document.RootElement.GetProperty("permissionDecisionReason").GetString()!;
    }

    private static string Copilot(string cwd, string command, string tool = "bash") => JsonSerializer.Serialize(new
    {
        cwd,
        agentId = "agent-7",
        parentAgentId = "root",
        agentType = "architect",
        toolName = tool,
        toolArgs = JsonSerializer.Serialize(new { command }),
    });

    private static string CopilotView(string cwd) => JsonSerializer.Serialize(new
    {
        cwd,
        agentId = "agent-7",
        parentAgentId = "root",
        agentType = "architect",
        toolName = "view",
        toolArgs = JsonSerializer.Serialize(new { path = Path.Combine(cwd, "src", "Job.cs") }),
    });

    /// <summary>The run-record store reads this only for a record's live branch, which the guard reads from its git files instead.</summary>
    private sealed class UnusedBranches : ICheckedOutBranchReader
    {
        public string? Read(string workingDirectory) => null;
    }

    private sealed class NoConfig : IHostConfigLoader
    {
        public HostConfigLoadResult TryLoad(string repoRoot) => new(false, null, null, "no project configuration");
    }

    private sealed class NoState : IArbiterStateFactory
    {
        public IArbiterLedger OpenLedger(string arbiterDirectory) => throw new NotSupportedException();

        public IArbiterDecisionLog OpenDecisionLog(string arbiterDirectory) => throw new NotSupportedException();

        public IArbiterRunStore OpenRunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, TimeProvider? clock = null) =>
            throw new NotSupportedException();
    }

    private sealed class NoBudgets : IWorkerBudgetStore
    {
        public void EnsureDirectory(string home) => throw new NotSupportedException();

        public bool Exists(string home, string key) => false;

        public string[] ReadLines(string home, string key) => [];

        public void Delete(string home, string key) => throw new NotSupportedException();

        public void TryCreate(string home, string key, string content) => throw new NotSupportedException();

        public void Append(string home, string key, string content) => throw new NotSupportedException();

        public void Sweep(string home, DateTimeOffset now, TimeSpan retention) => throw new NotSupportedException();
    }
}
