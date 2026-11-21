using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.FileSystem;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The worker guard on Factory Droid 1.6.0, where a tool-call payload names no calling sub-agent. The guard joins
/// the Factory session events into a ledger first: a root session's <c>Task</c> dispatch names the agent type, the
/// child session's <c>SessionStart</c> carries <c>calling_session_id</c>, and the dispatch's <c>PostToolUse</c> text
/// names the child session and its type. Every payload here is the exact shape the live probe recorded, with the ids
/// replaced.
/// </summary>
public sealed class WorkerGuardFactoryTests
{
    private const string Harness = "factory";
    private const string RootSession = "root-0001";
    private const string ChildSession = "child-0002";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly InMemoryFileSystem _files;
    private readonly FakeProcessEnvironment _environment;
    private readonly FileWorkerSessionStore _sessions;
    private readonly string _home;
    private readonly string _repo;

    public WorkerGuardFactoryTests()
    {
        _files = new InMemoryFileSystem(_time);
        _home = Path.Combine(_files.Root, "kw", "home");
        _repo = Path.Combine(_files.Root, "kw", "repo");
        _files.CreateDirectory(_home);
        _files.CreateDirectory(_repo);
        _environment = new FakeProcessEnvironment(
            currentDirectory: _repo,
            homeDirectory: _home,
            isFileSystemCaseInsensitive: !_files.CaseSensitive);
        _sessions = new FileWorkerSessionStore(_files);
    }

    [Fact]
    public void ARootSessionsExecuteOfASecretIsNeverGuarded()
    {
        Guard(RootStart());

        string answer = Guard(Execute("cat ~/.config/x/sa-password"));

        Assert.Empty(answer);
    }

    [Fact]
    public void AChildsExecuteOfASecretIsRefusedWithFactorysDenyDocument()
    {
        ResolveChild("dev-medium");

        string answer = Guard(Execute("cat ~/.config/x/sa-password"));

        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement output = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.StartsWith(
            "KW-ARB-SECRET-001",
            output.GetProperty("permissionDecisionReason").GetString(),
            StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("permissionDecision", out _));
    }

    [Fact]
    public void AChildsReadOfASecretFileIsRefused()
    {
        ResolveChild("dev-medium");

        string answer = Guard(Read("/home/operator/.config/x/sa-password"));

        Assert.StartsWith("KW-ARB-SECRET-001", Reason(answer), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoParallelTasksOfDifferentTypesAreResolvedByThePostToolUseText()
    {
        Guard(RootStart());
        Guard(TaskPre("dev-medium"));
        Guard(TaskPre("platform-researcher"));
        Guard(ChildStart());
        Guard(TaskPost("kid", "platform-researcher"));

        WorkerGuardRules.ToolCall? call = WorkerGuardEventReader.Read(
            Harness, Execute("echo ready", sessionId: "kid"), _sessions, _home, TextWriter.Null);

        Assert.NotNull(call);
        Assert.Equal("platform-researcher", call.Agent.Name);
        Assert.Equal(RootSession, call.SessionId);
    }

    [Fact]
    public void ThePostToolUseTextCorrectsTheTypeTheChildStartHadGuessed()
    {
        Guard(RootStart());
        Guard(TaskPre("dev-medium"));
        Guard(ChildStart());
        Guard(TaskPost(ChildSession, "platform-researcher"));

        WorkerGuardRules.ToolCall? call = WorkerGuardEventReader.Read(
            Harness, Execute("echo ready"), _sessions, _home, TextWriter.Null);

        Assert.NotNull(call);
        Assert.Equal("platform-researcher", call.Agent.Name);
    }

    [Fact]
    public void AnUnresolvedChildGetsOnlyTheEveryProfileRulesAndLogsTheSkippedOnes()
    {
        // Two types are in flight, so the child's SessionStart cannot say which one it is.
        Guard(RootStart());
        Guard(TaskPre("dev-medium"));
        Guard(TaskPre("platform-researcher"));
        Guard(ChildStart());

        using StringWriter log = new();
        string answer = Guard(Execute("az group delete --name db --resource-group rg"), new ImpatientSessions(_sessions), log);

        Assert.Empty(answer);
        Assert.Contains("profile-specific", log.ToString(), StringComparison.Ordinal);

        string secret = Guard(Execute("cat ~/.config/x/sa-password"), new ImpatientSessions(_sessions));
        Assert.StartsWith("KW-ARB-SECRET-001", Reason(secret), StringComparison.Ordinal);
    }

    [Fact]
    public void ASessionEndRemovesTheChildsEntry()
    {
        ResolveChild("dev-medium");

        Guard(SessionEvent("SessionEnd", ChildSession));

        Assert.Empty(Guard(Execute("cat ~/.config/x/sa-password")));
    }

    [Fact]
    public void ARootSessionsTaskDispatchAndPostAreRecordedButNeverGuarded()
    {
        Guard(RootStart());

        Assert.Empty(Guard(TaskPre("dev-medium")));
        Assert.Empty(Guard(TaskPost(ChildSession, "dev-medium")));
    }

    [Fact]
    public void ASessionEndRemovesTheRootsPendingList()
    {
        string pendingKey = WorkerSessionLedger.PendingKey(RootSession);
        Guard(RootStart());
        Guard(TaskPre("dev-medium"));
        Guard(TaskPre("platform-researcher"));
        Assert.NotEmpty(_sessions.ReadPending(_home, pendingKey));

        Guard(SessionEvent("SessionEnd", RootSession));

        Assert.Empty(_sessions.ReadPending(_home, pendingKey));
    }

    [Fact]
    public void AnEntryThatNeverResolvedAndNeverEndedIsRemovedByTheSweep()
    {
        string staleKey = WorkerSessionLedger.ChildKey("stale-child");
        Guard(RootStart());
        _sessions.EnsureDirectory(_home);
        _sessions.AppendParent(_home, staleKey, RootSession);
        Assert.True(_sessions.Exists(_home, staleKey));

        _time.Advance(TimeSpan.FromDays(3));

        // A budgeted child's first tool call is where the budget files and the ledger are swept together.
        Guard(TaskPre("dev-medium"));
        Guard(ChildStart());
        Guard(Execute("ls"));

        Assert.False(_sessions.Exists(_home, staleKey));
        Assert.True(_sessions.Exists(_home, WorkerSessionLedger.ChildKey(ChildSession)));
    }

    [Fact]
    public void AReturnForAChildThatAlreadyStandsForATypeTakesNoSecondPendingEntry()
    {
        // Two same-type dispatches: the first child takes its entry from the list when it starts, so its
        // return must not take the second dispatch's entry, which would leave the second child waiting.
        Guard(RootStart());
        Guard(TaskPre("dev-medium"));
        Guard(ChildStart());
        Guard(TaskPre("dev-medium"));

        Guard(TaskPost(ChildSession, "dev-medium"));

        Assert.Equal(
            "dev-medium",
            Assert.Single(WorkerSessionLedger.PendingTypes(_sessions.ReadPending(_home, WorkerSessionLedger.PendingKey(RootSession)))));
    }

    [Fact]
    public void TheCorrectionAfterTheChildStartStandsOnTheRealLedger()
    {
        using TempDirectory temp = new();
        string home = Path.Combine(temp.Path, "home");
        FileWorkerSessionStore store = new(new PhysicalFileSystem());

        Apply(home, store, RootStart());
        Apply(home, store, TaskPre("dev-medium"));
        Apply(home, store, ChildStart());
        Apply(home, store, TaskPost(ChildSession, "platform-researcher"));

        Assert.Equal("platform-researcher", store.Read(home, WorkerSessionLedger.ChildKey(ChildSession))?.AgentType);
    }

    [Fact]
    public void AChildStartAfterTheCorrectionKeepsTheCorrectionOnTheRealLedger()
    {
        using TempDirectory temp = new();
        string home = Path.Combine(temp.Path, "home");
        FileWorkerSessionStore store = new(new PhysicalFileSystem());

        Apply(home, store, RootStart());
        Apply(home, store, TaskPre("platform-researcher"));
        Apply(home, store, TaskPost(ChildSession, "platform-researcher"));
        Apply(home, store, ChildStart());

        Assert.Equal("platform-researcher", store.Read(home, WorkerSessionLedger.ChildKey(ChildSession))?.AgentType);
    }

    [Fact]
    public void TheCheapDecisionReadsOnlyTheEvent()
    {
        Assert.True(WorkerGuard.NeedsDecision(Harness, ChildStart()));
        Assert.True(WorkerGuard.NeedsDecision(Harness, Execute("ls")));
        Assert.True(WorkerGuard.NeedsDecision(Harness, TaskPre("dev-medium")));
        Assert.False(WorkerGuard.NeedsDecision(Harness, PostEvent("Read")));
        Assert.False(WorkerGuard.NeedsDecision(Harness, "{\"hook_event_name\":\"Notification\"}"));
        Assert.False(WorkerGuard.NeedsDecision("codex", Execute("ls")));
    }

    private void ResolveChild(string type)
    {
        Guard(RootStart());
        Guard(TaskPre(type));
        Guard(ChildStart());
    }

    private string Guard(string json, IWorkerSessionStore? sessions = null, TextWriter? log = null)
    {
        WorkerGuard guard = new(
            new FileWorkerBudgetStore(_files),
            new NoCheckouts(),
            new NoConfig(),
            _environment,
            TimeProvider.System,
            new NoState(),
            _files,
            sessions ?? _sessions);
        using StringWriter stdout = new();
        guard.Run(Harness, json, stdout, log ?? TextWriter.Null, _home, now: () => _time.GetUtcNow());
        return stdout.ToString();
    }

    /// <summary>Feeds one Factory event to the ledger alone, keeping the guard out of the real-store tests.</summary>
    private static void Apply(string home, IWorkerSessionStore sessions, string json) =>
        WorkerSessionLedger.Apply(Harness, json, home, sessions);

    private static string Reason(string answer)
    {
        Assert.NotEmpty(answer);
        using JsonDocument document = JsonDocument.Parse(answer);
        return document.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecisionReason").GetString()!;
    }

    private string RootStart() => JsonSerializer.Serialize(new
    {
        session_id = RootSession,
        cwd = _repo,
        permission_mode = "auto-high",
        hook_event_name = "SessionStart",
        source = "startup",
    });

    private string ChildStart() => SessionEvent("SessionStart", ChildSession, callingSessionId: RootSession);

    private static string SessionEvent(string eventName, string sessionId, string? callingSessionId = null) =>
        JsonSerializer.Serialize(new
        {
            session_id = sessionId,
            hook_event_name = eventName,
            source = "startup",
            calling_session_id = callingSessionId,
            cwd = "/kw/repo",
        });

    private static string TaskPre(string type) => JsonSerializer.Serialize(new
    {
        session_id = RootSession,
        hook_event_name = "PreToolUse",
        tool_name = "Task",
        tool_input = new { subagent_type = type, description = "work", prompt = "do it" },
        cwd = "/kw/repo",
    });

    private static string TaskPost(string childSession, string type) => JsonSerializer.Serialize(new
    {
        session_id = RootSession,
        hook_event_name = "PostToolUse",
        tool_name = "Task",
        tool_input = new { subagent_type = type, description = "work", prompt = "do it" },
        tool_response = $"Task launched in background.\ntask_id: {childSession}\nsession_id: {childSession}\n" +
            $"subagent_type: {type}\n",
        cwd = "/kw/repo",
    });

    private static string Execute(string command, string sessionId = ChildSession) => JsonSerializer.Serialize(new
    {
        session_id = sessionId,
        hook_event_name = "PreToolUse",
        tool_name = "Execute",
        tool_input = new { command, summary = "run", riskLevel = "low" },
        cwd = "/kw/repo",
    });

    private static string Read(string path) => JsonSerializer.Serialize(new
    {
        session_id = ChildSession,
        hook_event_name = "PreToolUse",
        tool_name = "Read",
        tool_input = new { file_path = path, line_numbers = true },
        cwd = "/kw/repo",
    });

    private static string PostEvent(string tool) => JsonSerializer.Serialize(new
    {
        session_id = ChildSession,
        hook_event_name = "PostToolUse",
        tool_name = tool,
        tool_response = "done",
        cwd = "/kw/repo",
    });

    /// <summary>
    /// The real ledger, with the type wait cut short: the production wait is the adapter's, and the Data project
    /// pins it. Here the fallback's decision is what matters, not three seconds of polling.
    /// </summary>
    private sealed class ImpatientSessions(IWorkerSessionStore inner) : IWorkerSessionStore
    {
        public void EnsureDirectory(string home) => inner.EnsureDirectory(home);

        public bool Exists(string home, string key) => inner.Exists(home, key);

        public WorkerSessionEntry? Read(string home, string key) => inner.Read(home, key);

        public WorkerSessionEntry? AwaitType(string home, string key, TimeSpan timeout) => inner.Read(home, key);

        public void AppendParent(string home, string key, string parentSessionId) => inner.AppendParent(home, key, parentSessionId);

        public void AppendGuess(string home, string key, string agentType) => inner.AppendGuess(home, key, agentType);

        public void AppendType(string home, string key, string agentType) => inner.AppendType(home, key, agentType);

        public void Delete(string home, string key) => inner.Delete(home, key);

        public void DeletePending(string home, string key) => inner.DeletePending(home, key);

        public string[] ReadPending(string home, string key) => inner.ReadPending(home, key);

        public void AppendPending(string home, string key, string entry) => inner.AppendPending(home, key, entry);

        public void Sweep(string home, DateTimeOffset now, TimeSpan retention) => inner.Sweep(home, now, retention);
    }
}
