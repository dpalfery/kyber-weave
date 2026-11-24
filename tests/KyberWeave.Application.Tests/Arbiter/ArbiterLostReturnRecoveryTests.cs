using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A dispatch whose return hook was lost (the harness killed it at its timeout) must not stay in flight for the rest
/// of the run. A harness's stop event closes the one dispatch that agent left open.
/// </summary>
/// <remarks>
/// In the live run this reproduces, the <c>postToolUse</c> hook of a finished <c>dev-easy</c> dispatch timed out, so
/// its <c>post</c> row was never written. The next attempt at the same task was refused with
/// <c>KW-ARB-READY-001</c> answer <c>overlaps-in-flight</c>, and nothing in the product could close the pre row, so
/// the run ended blocked. Copilot's <c>subagentStop</c> for that agent is the signal that the dispatch is over.
/// </remarks>
public sealed class ArbiterLostReturnRecoveryTests : IDisposable
{
    private const string Plan = "docs/plans/x.md";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    private readonly FakeArbiterProvider _provider = new()
    {
        DefaultAnswer = ArbiterStep1BatchResult.NotEvaluated,
        ScoreDefaultAnswer = true,
        Confidence = 0.95,
        Model = "stub",
    };

    public void Dispose() => _host.Dispose();

    private sealed class NoGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    /// <summary>Stands in for the plan: T2 and T5 are free, each declaring its own file, so one holds its own in flight.</summary>
    private sealed class PlanFacts : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification)
        {
            if (!classification.Headers.TryGetValue("TASK", out string? task))
            {
                return facts;
            }

            return facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
                .With("plan.tasks.count", 3, ArbiterFactLabel.Derived)
                .With("plan.development-mode", "standard", ArbiterFactLabel.Derived)
                .With("plan.task", task, ArbiterFactLabel.Derived)
                .With("plan.task.files", new List<string> { "src/" + task + ".cs" }, ArbiterFactLabel.Derived)
                .With("plan.task.depends-on", new List<string>(), ArbiterFactLabel.Derived)
                .With("plan.test-contract.row", "A failing test", ArbiterFactLabel.Derived);
        }
    }

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = new ArbiterConfig { Enabled = true, Rules = ArbiterConfigDefaults.Product.Rules },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private static string Prompt(string task) =>
        $"KYBER-ARBITER: true\nPLAN_FILE: {Plan}\nTASK: {task}\n\nWork on {task}.";

    private ArbiterHookDecisionEngine Engine() => _host.Engine(
        providerFactory: _ => _provider,
        gitFactsFactory: _ => new NoGitFacts(),
        planReaderFactory: _ => new PlanFacts(),
        homeDirectory: () => _host.RepoRoot);

    private HookContext Context(TextWriter log) => new(_host.RepoRoot, () => "decision-1", log);

    /// <summary>Moves the host clock on, so a later event is measurably later than the rows already written.</summary>
    private void Advance() => _host.Clock.Advance(TimeSpan.FromSeconds(1));

    private void Dispatch(string target, string task, string toolCallId, TextWriter log) =>
        Engine().DecidePreDispatch(
            "copilot-cli", "conductor", target, Prompt(task), Config(), Context(log),
            toolCallId, sessionId: "session-1", dispatchModel: null);

    private void Return(string task, string toolCallId, TextWriter log) =>
        Engine().DecidePostDispatch(
            "copilot-cli", "conductor", "dev-easy", Prompt(task), "All done.\nSTATUS: PASS\nRESULT: PASS\n",
            Config(), Context(log), toolCallId, sessionId: "session-1");

    /// <summary>
    /// Sends the Copilot CLI <c>subagentStop</c> of a finished agent through the real hook command, with no response
    /// text, so the pass-through decision and the adapter's own path are both exercised.
    /// </summary>
    private string Stop(string agent, TextWriter log, string sessionId = "session-1")
    {
        var payload = new
        {
            sessionId,
            cwd = _host.RepoRoot,
            agentId = "agent-" + agent,
            agentType = agent,
            agentName = agent,
            agentDisplayName = agent,
            stopReason = "end_turn",
            stop_hook_active = false,
        };
        StringWriter stdout = new();
        HookCommand command = _host.HookCommand(
            ArbiterTestServices.Registry(Engine()),
            configLoader: _ => Config());
        int exit = command.Run("copilot-cli", "conductor", JsonSerializer.Serialize(payload), stdout, log);
        Assert.Equal(0, exit);
        return stdout.ToString();
    }

    private string Answer(string ruleId) =>
        Assert.Single(
            _host.Decisions.Last(record => record.Rules.Any(rule => rule.Id == ruleId)).Rules,
            rule => rule.Id == ruleId).Answer;

    /// <summary>The engine over the test's ledger log: the paired reads see the host's rows, the recovery append
    /// answers the way the test's log answers.</summary>
    private ArbiterHookDecisionEngine EngineOver(IArbiterLogStore log) => new(
        _host.Get<IArbiterEvaluatorFactory>(),
        new LedgerStateOver(_host.State, log),
        _host.Get<IArbiterProviderFactory>(),
        _host.Get<IArbiterUserSettings>(),
        _host.Get<IPlanFacts>(),
        _host.Get<IPlanRootResolver>(),
        _host.Get<IHookGitFactsFactory>(),
        _host.Get<ICredentialStore>(),
        _host.Get<IArbiterHostConfig>(),
        _host.Get<IProcessEnvironment>(),
        _host.Get<IBackgroundDispatchStore>(),
        _host.Get<IIdGenerator>(),
        new ReviewSeatRecorder(_host.Get<IWritableFileSystem>(), _host.Get<IPathResolver>()),
        providerFactory: _ => _provider,
        gitFactsFactory: _ => new NoGitFacts(),
        planReaderFactory: _ => new PlanFacts(),
        homeDirectory: () => _host.RepoRoot,
        clock: _host.Clock);

    /// <summary>The state factory the bound test's engine opens: the ledger over the test's log, the decision log
    /// over the host's.</summary>
    private sealed class LedgerStateOver(InMemoryArbiterState inner, IArbiterLogStore log) : IArbiterStateFactory
    {
        public IArbiterLedger OpenLedger(string arbiterDirectory) => new InFlightLedger(arbiterDirectory, log);

        public IArbiterDecisionLog OpenDecisionLog(string arbiterDirectory) => inner.Decisions(arbiterDirectory);

        public IArbiterRunStore OpenRunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, TimeProvider? clock = null) =>
            inner.RunStore(arbiterDirectory, options, clock);
    }

    /// <summary>An append that never answers unless its token is cancelled: the recovery wait must carry its own bound.</summary>
    private sealed class NeverAnsweringLogStore(IArbiterLogStore inner) : IArbiterLogStore
    {
        public Task AppendLineAsync(string directory, string fileName, string line, CancellationToken cancellationToken) =>
            Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);

        public Task AppendLineAsync(
            string directory,
            string fileName,
            long snapshotLength,
            Func<IReadOnlyList<string>, string> lineFactory,
            CancellationToken cancellationToken) =>
            inner.AppendLineAsync(directory, fileName, snapshotLength, lineFactory, cancellationToken);

        public long ByteLength(string path) => inner.ByteLength(path);

        public IReadOnlyList<string> ReadCompleteLines(string path) => inner.ReadCompleteLines(path);

        public LogTail ReadComplete(string path) => inner.ReadComplete(path);

        public IReadOnlyList<string> ReadTailLines(string path, long tailBytes, bool keepUnterminatedFinalLine) =>
            inner.ReadTailLines(path, tailBytes, keepUnterminatedFinalLine);

        public LogTail ReadTail(string path, long tailBytes, bool keepUnterminatedFinalLine) =>
            inner.ReadTail(path, tailBytes, keepUnterminatedFinalLine);
    }

    private IReadOnlyList<ArbiterLedgerEvent> Events() => _host.Ledger.ReadAll();

    private IReadOnlyList<ArbiterLedgerEvent> RecoveredPosts() =>
        [.. Events().Where(e => e.Phase == ArbiterLedgerPhases.Post && e.Recovered is not null)];

    [Fact]
    public void WithoutTheStopEvent_TheFixRoundIsRefusedBecauseTheLostDispatchLooksInFlight()
    {
        TextWriter log = TextWriter.Null;
        Dispatch("dev-easy", "T2", "call-1", log);

        // This is the live failure, reproduced without the stop event: the earlier pre has no post, so its task
        // files still look held by a worker that finished long ago.
        Dispatch("dev-easy", "T2", "call-2", log);

        Assert.Equal("overlaps-in-flight", Answer("KW-ARB-READY-001"));
        Assert.Empty(RecoveredPosts());
    }

    [Fact]
    public void TheStopEvent_ClosesTheLostDispatch_AndTheFixRoundIsAllowed()
    {
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        ArbiterLedgerEvent lost = Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre);

        Advance();
        string answer = Stop("dev-easy", log);

        // The stop hook writes nothing to the harness: it closes the ledger row.
        Assert.Equal(string.Empty, answer);
        ArbiterLedgerEvent recovered = Assert.Single(RecoveredPosts());
        Assert.Equal(lost.Id, recovered.PreId);
        Assert.Equal(ArbiterLedgerPhases.Post, recovered.Phase);
        Assert.Equal("dev-easy", recovered.Target);
        Assert.Contains("subagentStop", recovered.Recovered, StringComparison.Ordinal);
        Assert.Contains("closed", log.ToString(), StringComparison.OrdinalIgnoreCase);

        Dispatch("dev-easy", "T2", "call-2", log);

        Assert.Equal("ready", Answer("KW-ARB-READY-001"));
    }

    [Fact]
    public void AStopEventBeforeTheRealPost_DoesNotStealItsReturn()
    {
        // Copilot CLI fires subagentStop at the end of a synchronous agent's turn, before the parent's postToolUse.
        // The stop closes the dispatch first; the real post must still claim it and carry the report, or every
        // healthy synchronous dispatch would lose its task completion.
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        ArbiterLedgerEvent lost = Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre);
        Advance();
        Stop("dev-easy", log);

        Engine().DecidePostDispatch(
            "copilot-cli", "conductor", "dev-easy", Prompt("T2"), "All done.\nSTATUS: PASS\nRESULT: PASS\n",
            Config(), Context(log), toolCallId: "call-1", sessionId: "session-1");

        List<ArbiterLedgerEvent> posts = [.. Events().Where(e => e.Phase == ArbiterLedgerPhases.Post)];
        Assert.Equal(2, posts.Count);
        ArbiterLedgerEvent report = Assert.Single(posts, e => e.Recovered is null);
        Assert.Equal(lost.Id, report.PreId);
        Assert.Equal("PASS", report.Returns?.TaskReview);
        Assert.Contains("T2", _host.Ledger.CompletedTasks(Plan));
    }

    [Fact]
    public void TwoDispatchesOfTheSameAgentStillOpen_CloseNone_AndLogOneLine()
    {
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        Dispatch("dev-easy", "T5", "call-2", log);

        Advance();
        Stop("dev-easy", log);

        Assert.Empty(RecoveredPosts());
        string[] lines = log.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("dev-easy", StringComparison.Ordinal))
            .ToArray();
        string line = Assert.Single(lines);
        Assert.Contains("2", line, StringComparison.Ordinal);

        // Both pres stay open: closing the wrong one would report a task as finished that never ran.
        IReadOnlyList<ArbiterLedgerEvent> open = [.. Events().Where(e => e.Phase == ArbiterLedgerPhases.Pre)];
        Assert.Equal(2, open.Count);
        Assert.DoesNotContain(Events(), e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null);
    }

    [Fact]
    public void AStopFromAnotherAgent_ClosesNothing()
    {
        // A dispatch's target is its identity. The stop event of a sibling agent, and one naming the dispatcher
        // itself (the pre row's caller), are no evidence that this worker's turn is over.
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        ArbiterLedgerEvent pre = Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre);

        Advance();
        Stop("dev-hard", log);
        Advance();
        Stop("conductor", log);

        Assert.Empty(RecoveredPosts());
        Assert.Equal(pre.Id, Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre).Id);
    }

    [Fact]
    public void ALateStop_AfterASameTargetPost_ClosesNothing()
    {
        // Two same-target dispatches overlap. The first one's post lands, and only then does the stop event for its
        // agent arrive. That stop is a late signal for the dispatch that already returned; the still-open dispatch
        // beside it may be a different worker that is still running.
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        Advance();
        Dispatch("dev-easy", "T5", "call-2", log);
        Advance();
        Return("T2", "call-1", log);
        Advance();
        Stop("dev-easy", log);

        Assert.Empty(RecoveredPosts());
        string[] pairedPreIds = [.. Events()
            .Where(e => e.Phase == ArbiterLedgerPhases.Post && e.PreId is not null)
            .Select(e => e.PreId!)];
        Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre && !pairedPreIds.Contains(e.Id));
        Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Post);
    }

    [Fact]
    public void AStopWhoseOwnUnpairedReturnWasJustRecorded_StillClosesTheLostDispatch()
    {
        // Factory's stop path records the return before it asks for recovery. A return that pairs with nothing is not
        // evidence of some other finished dispatch: it is the lost return this recovery exists for.
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        ArbiterLedgerEvent lost = Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre);

        Advance();
        Engine().DecidePostDispatch(
            "copilot-cli", "conductor", "dev-easy", Prompt("T9"), "All done.\nRESULT: PASS\n",
            Config(), Context(log), toolCallId: "call-9", sessionId: "session-1");
        Advance();
        Stop("dev-easy", log);

        Assert.Equal(lost.Id, Assert.Single(RecoveredPosts()).PreId);
    }

    [Fact]
    public void AStopFromAnotherSession_ClosesNothing()
    {
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);

        Advance();
        Stop("dev-easy", log, sessionId: "session-2");

        Assert.Empty(RecoveredPosts());
    }

    [Fact]
    public void ACancelledRecoveryAppend_ClosesNothing_AndLogsOneLine()
    {
        // The recovery row is written on the stop hook, which the harness kills at its timeout. When the append
        // cannot finish, the recovery closes nothing loudly instead of throwing out of the hook.
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        ArbiterLedgerEvent pre = Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre);

        Advance();
        _host.State.Log.AppendFailure = new OperationCanceledException();
        int closed = Engine().RecoverLostReturn("copilot-cli", "session-1", ["dev-easy"], Context(log));

        Assert.Equal(0, closed);
        Assert.Empty(RecoveredPosts());
        Assert.Equal(pre.Id, Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre).Id);
        string line = Assert.Single(
            log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.Contains("closed nothing", StringComparison.Ordinal));
        Assert.Contains("subagentStop", line, StringComparison.Ordinal);
        Assert.Contains(pre.Id, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecoveryAppendThatNeverAnswers_DoesNotOutlastTheHookBudget()
    {
        // A recovery whose ledger append never answers must not hold the stop hook past its timeout: the harness
        // kills a hook past its budget, which would lose the very row the recovery exists to write.
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);

        Advance();
        ArbiterHookDecisionEngine engine = EngineOver(new NeverAnsweringLogStore(_host.State.Log));
        Task<int> recovery = Task.Run(
            () => engine.RecoverLostReturn("copilot-cli", "session-1", ["dev-easy"], Context(log)));
        Task finished = await Task.WhenAny(recovery, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.Same(recovery, finished);
        Assert.Equal(0, await recovery);
        Assert.Empty(RecoveredPosts());
    }

    [Fact]
    public async Task APreThatNamesNoSession_IsNeverRecovered()
    {
        // Rows written before the hook recorded the session cannot be matched to a stop event, and closing one
        // would guess which session's agent it belonged to.
        await _host.Ledger.AppendAsync(
            new ArbiterLedgerEvent(
                "legacy-pre-1",
                _host.Clock.GetUtcNow(),
                ArbiterSources.Hook,
                "copilot-cli",
                null,
                ArbiterLedgerPhases.Pre)
            {
                Target = "dev-easy",
            });

        Advance();
        Stop("dev-easy", TextWriter.Null);

        Assert.Empty(RecoveredPosts());
        Assert.Contains(Events(), e => e.Phase == ArbiterLedgerPhases.Pre && e.Id == "legacy-pre-1");
    }

    [Fact]
    public void ACopilotPreRow_CarriesTheEventsSessionId()
    {
        // The Copilot CLI event carries sessionId; every pre row records it, so recovery can require the same
        // session rather than closing a same-named agent's dispatch from another session.
        StringWriter stdout = new();
        HookCommand command = _host.HookCommand(
            ArbiterTestServices.Registry(Engine()),
            configLoader: _ => Config());
        string payload = JsonSerializer.Serialize(new
        {
            sessionId = "session-1",
            cwd = _host.RepoRoot,
            toolCalls = new[]
            {
                new
                {
                    id = "call-1",
                    name = "task",
                    args = new { agent_type = "dev-easy", name = "t2", prompt = Prompt("T2") },
                },
            },
        });

        int exit = command.Run("copilot-cli", "conductor", payload, stdout, TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Equal(
            "session-1",
            Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre).Session);
    }

    /// <summary>Remembers a dispatch as a followed background agent, the way the harness adapter does on its launch.</summary>
    private void Follow(string task, string toolCallId, string agentId, TextWriter log) =>
        Engine().TrackBackgroundDispatch(
            new BackgroundDispatch("copilot-cli", "session-1", agentId, "conductor", "dev-easy", Prompt(task), toolCallId),
            Context(log));

    [Fact]
    public void AStopThatNamesAFollowedAgentId_ClosesThatDispatchAmongSeveralOfTheSameName()
    {
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        Advance();
        Dispatch("dev-easy", "T5", "call-2", log);
        Follow("T2", "call-1", "agent-a", log);
        Follow("T5", "call-2", "agent-b", log);
        ArbiterLedgerEvent second = Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre && e.CallId == "call-2");

        Advance();
        int closed = Engine().RecoverLostReturn("copilot-cli", "session-1", ["dev-easy"], Context(log), "agent-b");

        Assert.Equal(1, closed);
        ArbiterLedgerEvent recovered = Assert.Single(RecoveredPosts());
        Assert.Equal(second.Id, recovered.PreId);
        Assert.Equal(second.Headers, recovered.Headers);
    }

    [Fact]
    public void AStopThatNamesAnAgentIdWhosePreIsAlreadyPaired_ClosesNothing()
    {
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        Dispatch("dev-easy", "T5", "call-2", log);
        Follow("T2", "call-1", "agent-a", log);
        Follow("T5", "call-2", "agent-b", log);
        Advance();
        Return("T2", "call-1", log);

        Advance();
        int closed = Engine().RecoverLostReturn("copilot-cli", "session-1", ["dev-easy"], Context(log), "agent-a");

        Assert.Equal(0, closed);
        Assert.Empty(RecoveredPosts());
    }

    [Fact]
    public void AStopThatNamesAnUnknownAgentId_FallsBackToTheNameRule()
    {
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        ArbiterLedgerEvent only = Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre);

        Advance();
        int closed = Engine().RecoverLostReturn("copilot-cli", "session-1", ["dev-easy"], Context(log), "agent-unknown");

        Assert.Equal(1, closed);
        Assert.Equal(only.Id, Assert.Single(RecoveredPosts()).PreId);
    }

    [Fact]
    public void AStopThatNamesAFollowedAgentId_IsNotHeldBackByASiblingThatAlreadyReturned()
    {
        // The name rule treats this stop as the late signal of the sibling that returned and closes nothing. An agent
        // id says exactly which dispatch ended, so the late-stop check does not apply.
        StringWriter log = new();
        Dispatch("dev-easy", "T2", "call-1", log);
        Advance();
        Dispatch("dev-easy", "T5", "call-2", log);
        Follow("T2", "call-1", "agent-a", log);
        Follow("T5", "call-2", "agent-b", log);
        ArbiterLedgerEvent open = Assert.Single(Events(), e => e.Phase == ArbiterLedgerPhases.Pre && e.CallId == "call-2");
        Advance();
        Return("T2", "call-1", log);

        Advance();
        int closed = Engine().RecoverLostReturn("copilot-cli", "session-1", ["dev-easy"], Context(log), "agent-b");

        Assert.Equal(1, closed);
        Assert.Equal(open.Id, Assert.Single(RecoveredPosts()).PreId);
    }
}
