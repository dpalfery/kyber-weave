using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Contracts.Repositories;
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
}
