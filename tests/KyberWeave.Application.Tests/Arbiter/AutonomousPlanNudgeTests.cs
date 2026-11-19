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

    private HookOutcome Return(ArbiterHookDecisionEngine engine, HookContext context, string output, string target = "architect", string callId = "call-1")
    {
        engine.DecidePreDispatch(Harness, Caller, target, Prompt, Config(), context, callId, "s1");
        return engine.DecidePostDispatch(Harness, Caller, target, Prompt, output, Config(), context, callId, "s1");
    }

    [Fact]
    public void AnAutonomousPlanReadyReturnCarriesTheNote()
    {
        HookOutcome outcome = Return(Engine(), Context("autonomous"), Ready);

        Assert.Equal(HookOutcomeKind.PostAnnotation, outcome.Kind);
        Assert.Equal(Note, outcome.Reason);
    }

    [Theory]
    [InlineData("supervised")]
    [InlineData(null)]
    public void SupervisedOrUnknownAutonomyGetsNoNote(string? autonomy)
    {
        HookOutcome outcome = Return(Engine(), Context(autonomy), Ready);

        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void ARunWithNoRecordGetsNoNote()
    {
        HookOutcome outcome = Return(Engine(), Context(null, withRun: false), Ready);

        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void APlannerReturnWithoutPlanReadyGetsNoNote()
    {
        HookOutcome outcome = Return(Engine(), Context("autonomous"), "Need answers.\nSTATUS: NEEDS_INPUT");

        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void ANonPlannerReturnGetsNoNote()
    {
        HookOutcome outcome = Return(Engine(), Context("autonomous"), Ready, target: "dev-medium");

        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void TheSameReturnRecordedTwiceIsNudgedOnce()
    {
        ArbiterHookDecisionEngine engine = Engine();
        HookContext context = Context("autonomous");
        engine.DecidePreDispatch(Harness, Caller, "architect", Prompt, Config(), context, "call-1", "s1");

        HookOutcome first = engine.DecidePostDispatch(Harness, Caller, "architect", Prompt, Ready, Config(), context, "call-1", "s1");
        HookOutcome second = engine.DecidePostDispatch(Harness, Caller, "architect", Prompt, Ready, Config(), context, "call-1", "s1");

        Assert.Equal(Note, first.Reason);
        Assert.Null(second.Reason);
    }

    [Fact]
    public void ABackgroundPlanReadyReturnHandsTheNoteOverOnTheNextDispatch()
    {
        ArbiterHookDecisionEngine engine = Engine();
        HookContext context = Context("autonomous");
        engine.DecidePreDispatch(Harness, Caller, "architect", Prompt, Config(), context, "call-1", "s1");
        engine.TrackBackgroundDispatch(new BackgroundDispatch(Harness, "s1", "agent-1", Caller, "architect", Prompt, "call-1"), context);

        engine.DecideBackgroundReturn(Harness, "s1", "agent-1", Ready, Config(), context);
        IReadOnlyList<string> notes = engine.TakePendingNotes("s1", Caller, context);

        string text = Assert.Single(notes);
        Assert.Contains(Note, text, StringComparison.Ordinal);
        Assert.Empty(engine.TakePendingNotes("s1", Caller, context));
    }

    [Fact]
    public void ASupervisedBackgroundPlanReadyReturnLeavesNoNote()
    {
        ArbiterHookDecisionEngine engine = Engine();
        HookContext context = Context("supervised");
        engine.DecidePreDispatch(Harness, Caller, "architect", Prompt, Config(), context, "call-1", "s1");
        engine.TrackBackgroundDispatch(new BackgroundDispatch(Harness, "s1", "agent-1", Caller, "architect", Prompt, "call-1"), context);

        engine.DecideBackgroundReturn(Harness, "s1", "agent-1", Ready, Config(), context);

        Assert.Empty(engine.TakePendingNotes("s1", Caller, context));
    }
}
