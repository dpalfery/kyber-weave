using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// In an autonomous run the Arbiter attaches one note to the architect's <c>PLAN_READY</c> return, telling the
/// conductor to finalize the plan instead of asking the user to approve it. Supervised and unknown autonomy, other
/// returns, and a repeat of the same return get none.
/// </summary>
public sealed class AutonomousPlanNudgeTests : IDisposable
{
    private const string Harness = "copilot-cli";
    private const string Caller = "conductor";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/p.md\n\nDraft the plan.";
    private const string Ready = "Plan drafted.\nSTATUS: PLAN_READY";
    private const string Note =
        "AUTONOMOUS RUN: the plan is ready. Call docs_finalize_approved_plan_and_write_task_file now with the approval "
        + "'AUTONOMOUS_APPROVAL: plan-approve-execute (autonomous: recommendation accepted)'. Do not ask the user to "
        + "approve; then dispatch the ready tasks.";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private sealed class PassThroughGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    private sealed class PassThroughPlanReader : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) => facts;
    }

    private static KyberWeaveConfig Config() =>
        new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = [] },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

    private ArbiterHookDecisionEngine Engine() => _host.Engine(
        providerFactory: _ => FakeArbiterProvider.Answering("x", 0.95, threshold: 0.9),
        gitFactsFactory: _ => new PassThroughGitFacts(),
        planReaderFactory: _ => new PassThroughPlanReader(),
        homeDirectory: () => _host.RepoRoot);

    private HookContext Context(string? autonomy, bool withRun = true)
    {
        string root = _host.RepoRoot;
        RunRecord? run = withRun
            ? new RunRecord(root + "/.git", "claude/feat/x", root, null, Autonomy: autonomy)
            : null;
        HookRunContext runContext = new(
            root, null, run, root, [root], null, null, null, false, HookRunContextSource.ActiveRun, []);
        return new HookContext(root, () => "decision-nudge", TextWriter.Null, RunContext: runContext);
    }

    private async Task<HookOutcome> Return(ArbiterHookDecisionEngine engine, HookContext context, string output, string target = "architect", string callId = "call-1")
    {
        await engine.DecidePreDispatchAsync(Harness, Caller, target, Prompt, Config(), context, callId, "s1");
        return await engine.DecidePostDispatchAsync(Harness, Caller, target, Prompt, output, Config(), context, callId, "s1");
    }

    [Fact]
    public async Task AnAutonomousPlanReadyReturnCarriesTheNote()
    {
        HookOutcome outcome = await Return(Engine(), Context("autonomous"), Ready);

        Assert.Equal(HookOutcomeKind.PostAnnotation, outcome.Kind);
        Assert.Equal(Note, outcome.Reason);
    }

    [Theory]
    [InlineData("supervised")]
    [InlineData(null)]
    public async Task SupervisedOrUnknownAutonomyGetsNoNote(string? autonomy)
    {
        HookOutcome outcome = await Return(Engine(), Context(autonomy), Ready);

        Assert.Null(outcome.Reason);
    }

    [Fact]
    public async Task ARunWithNoRecordGetsNoNote()
    {
        HookOutcome outcome = await Return(Engine(), Context(null, withRun: false), Ready);

        Assert.Null(outcome.Reason);
    }

    /// <summary>Several live runs with no signal choosing one leave no run to read the autonomy from, so no note is added.</summary>
    [Fact]
    public async Task AnAmbiguousRunContextGetsNoNote()
    {
        string root = _host.RepoRoot;
        HookRunContext ambiguous = new(
            root, null, null, root, [root], null, null, null, false, HookRunContextSource.Ambiguous, []);
        HookContext context = new(root, () => "decision-nudge", TextWriter.Null, RunContext: ambiguous);

        HookOutcome outcome = await Return(Engine(), context, Ready);

        Assert.Null(outcome.Reason);
    }

    [Fact]
    public async Task APlannerReturnWithoutPlanReadyGetsNoNote()
    {
        HookOutcome outcome = await Return(Engine(), Context("autonomous"), "Need answers.\nSTATUS: NEEDS_INPUT");

        Assert.Null(outcome.Reason);
    }

    [Fact]
    public async Task ANonPlannerReturnGetsNoNote()
    {
        HookOutcome outcome = await Return(Engine(), Context("autonomous"), Ready, target: "dev-medium");

        Assert.Null(outcome.Reason);
    }

    [Fact]
    public async Task TheSameReturnRecordedTwiceIsNudgedOnce()
    {
        ArbiterHookDecisionEngine engine = Engine();
        HookContext context = Context("autonomous");
        await engine.DecidePreDispatchAsync(Harness, Caller, "architect", Prompt, Config(), context, "call-1", "s1");

        HookOutcome first = await engine.DecidePostDispatchAsync(Harness, Caller, "architect", Prompt, Ready, Config(), context, "call-1", "s1");
        HookOutcome second = await engine.DecidePostDispatchAsync(Harness, Caller, "architect", Prompt, Ready, Config(), context, "call-1", "s1");

        Assert.Equal(Note, first.Reason);
        Assert.Null(second.Reason);
    }

    [Fact]
    public async Task ADuplicateReturnWithoutACallIdIsFoundByItsPreDispatch()
    {
        // A harness that reports no tool-call id leaves CallId null on the ledger event, so the
        // repeat is recognised by the pre event both returns were matched to.
        ArbiterLedgerEvent earlier = PostEvent("post-1", callId: "call-1", preId: "pre-1");
        ArbiterLedgerEvent current = PostEvent("post-2", callId: null, preId: "pre-1");

        Assert.True(AutonomousPlanNudge.AlreadyNudged([earlier, current], current, callId: null));
    }

    [Fact]
    public async Task AReturnFromADifferentPreDispatchIsStillNudged()
    {
        ArbiterLedgerEvent earlier = PostEvent("post-1", callId: "call-1", preId: "pre-1");
        ArbiterLedgerEvent current = PostEvent("post-2", callId: null, preId: "pre-2");

        Assert.False(AutonomousPlanNudge.AlreadyNudged([earlier, current], current, callId: null));
    }

    private static ArbiterLedgerEvent PostEvent(string id, string? callId, string? preId) => new(
        id,
        DateTimeOffset.UnixEpoch,
        "hook",
        Harness,
        "s1",
        ArbiterLedgerPhases.Post)
    {
        CallId = callId,
        PreId = preId,
        Target = "architect",
        Trigger = AutonomousPlanNudge.Trigger,
        Returns = new ArbiterReturnMarkers(PlannerStatus: "PLAN_READY"),
    };

    [Fact]
    public async Task ABackgroundPlanReadyReturnHandsTheNoteOverOnTheNextDispatch()
    {
        ArbiterHookDecisionEngine engine = Engine();
        HookContext context = Context("autonomous");
        await engine.DecidePreDispatchAsync(Harness, Caller, "architect", Prompt, Config(), context, "call-1", "s1");
        engine.TrackBackgroundDispatch(new BackgroundDispatch(Harness, "s1", "agent-1", Caller, "architect", Prompt, "call-1"), context);

        await engine.DecideBackgroundReturnAsync(Harness, "s1", "agent-1", Ready, Config(), context);
        IReadOnlyList<string> notes = engine.TakePendingNotes("s1", Caller, context);

        string text = Assert.Single(notes);
        Assert.Contains(Note, text, StringComparison.Ordinal);
        Assert.Empty(engine.TakePendingNotes("s1", Caller, context));
    }

    [Fact]
    public async Task ASupervisedBackgroundPlanReadyReturnLeavesNoNote()
    {
        ArbiterHookDecisionEngine engine = Engine();
        HookContext context = Context("supervised");
        await engine.DecidePreDispatchAsync(Harness, Caller, "architect", Prompt, Config(), context, "call-1", "s1");
        engine.TrackBackgroundDispatch(new BackgroundDispatch(Harness, "s1", "agent-1", Caller, "architect", Prompt, "call-1"), context);

        await engine.DecideBackgroundReturnAsync(Harness, "s1", "agent-1", Ready, Config(), context);

        Assert.Empty(engine.TakePendingNotes("s1", Caller, context));
    }
}
