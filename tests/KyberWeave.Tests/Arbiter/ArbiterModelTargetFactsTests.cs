using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 2.1: the model-targeting facts the <c>delegate.model</c> secondary trigger reads.
/// RED: <c>SecondaryTriggers.For</c> returns no secondary trigger; <c>ModelTargetFacts</c>,
/// <c>ArbiterPriorAttempt</c>, <c>ArbiterFileKind</c>, <c>ArbiterModelBlock</c> and the
/// <c>delegationModel</c> parameter of <c>TriggerFactBuilder.Build</c> do not exist yet.
/// </summary>
/// <remarks>
/// Assumed seams (named so the GREEN task can match them): <c>ModelTargetFacts.Enrich(facts, ledger,
/// decisions, ledgerId, targeting)</c> fills <c>plan.task.file-kinds</c>,
/// <c>ledger.task.prior-attempt</c> and <c>model-targeting.ladder.next</c> on a bag that already carries
/// <c>plan.task.files</c>, <c>plan.task.file-kinds</c> is a list of <c>ArbiterFileKind(Extension, Kind)</c>,
/// and <c>ledger.task.prior-attempt</c> holds an <c>ArbiterPriorAttempt(Result, Profile, Token)</c>.
/// </remarks>
public sealed class ArbiterModelTargetFactsTests : IDisposable
{
    private const string Plan = "docs/plans/x.md";
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";
    private const string CurrentLedgerId = "ledger-now";

    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = new ArbiterConfig { Enabled = true, Rules = ArbiterConfig.ProductDefaults.Rules },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private static ArbiterModelTargetingConfig Targeting() => new(
        "shadow",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["easy"] = "fast",
            ["standard"] = "general",
            ["hard"] = "deep-planning",
        },
        ["fast", "general", "deep-planning"]);

    /// <summary>The <c>delegate.model</c> bag for T2, with the plan's task files and the ledger facts it reads.</summary>
    private static ArbiterFactSet DelegateModelBag(IReadOnlyList<string>? taskFiles, string? delegationModel = null)
    {
        ArbiterFactSet bag = TriggerFactBuilder.Build(
            "delegate.model", "csharp-dev", Prompt, "conductor", "harness", Config(),
            delegationModel: delegationModel);
        bag = bag.With("plan.task", "T2", ArbiterFactLabel.Derived);
        return taskFiles is null
            ? bag
            : bag.With("plan.task.files", taskFiles, ArbiterFactLabel.Derived);
    }

    private static ArbiterDecisionRecord Decision(string id, string ledgerId, ArbiterModelBlock? model) =>
        new(
            id,
            At,
            "hook",
            "claude",
            null,
            ledgerId,
            "delegate",
            "conductor",
            "header",
            "csharp-dev",
            Plan,
            null,
            "T2",
            "shipped",
            [],
            new ArbiterProviderRecord("none", null, null, null, null, ArbiterProviderStatuses.NotEvaluated),
            RuleEffects.Allow,
            0,
            1,
            model: model);

    private static ArbiterLedgerEvent Pre(string id, string task, string? profile, string? token, string? source = "model-targets") =>
        new(id, At, "hook", "claude", null, ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            Target = "csharp-dev",
            Headers = new ArbiterLedgerHeaders(PlanFile: Plan, Task: task),
            ModelProfile = profile,
            ModelToken = token,
            ModelSource = source,
        };

    private static ArbiterLedgerEvent Post(string id, string preId, string result) =>
        new(id, At, "hook", "claude", null, ArbiterLedgerPhases.Post)
        {
            PreId = preId,
            Returns = new ArbiterReturnMarkers(TaskReview: result),
        };

    private static ArbiterModelBlock Block(string profile, string? token, bool delivered) =>
        new(profile, token, token, "ladder", delivered, null);

    private ArbiterFactSet Enrich(ArbiterFactSet bag) =>
        ModelTargetFacts.Enrich(
            bag,
            new InFlightLedger(ArbiterDirectory),
            new DecisionLog(ArbiterDirectory),
            CurrentLedgerId,
            Targeting());

    private static ArbiterPriorAttempt PriorAttempt(ArbiterFactSet facts)
    {
        ArbiterFact fact = Assert.Single(facts.Facts, pair => pair.Key == "ledger.task.prior-attempt").Value;
        return Assert.IsType<ArbiterPriorAttempt>(fact.Value);
    }

    // 1. delegate.model derives from delegate events to implementation-class targets, and from nothing else.

    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("test-dev")]
    [InlineData("react-dev")]
    public void SecondaryTriggers_DelegateToAnImplementationTarget_DerivesDelegateModel(string target)
    {
        ArbiterEvent ev = new() { Harness = "claude", Caller = "conductor", Target = target, Prompt = Prompt };

        IReadOnlyList<string> secondaries = SecondaryTriggers.For("delegate", ev);

        Assert.Contains("delegate.model", secondaries);
    }

    [Theory]
    [InlineData("delegate.planner", "architect")]
    [InlineData("investigate", "bug-crusher-investigator")]
    [InlineData("investigate", "bug-crusher")]
    [InlineData("investigate", "code-reviewer")]
    [InlineData("investigate", "conductor")]
    [InlineData("delegate", "architect")]
    [InlineData("delegate", "no-such-agent")]
    public void SecondaryTriggers_EveryOtherDispatch_NeverDerivesDelegateModel(string primary, string target)
    {
        ArbiterEvent ev = new() { Harness = "claude", Caller = "conductor", Target = target, Prompt = Prompt };

        IReadOnlyList<string> secondaries = SecondaryTriggers.For(primary, ev);

        Assert.DoesNotContain("delegate.model", secondaries);
    }

    // 2. plan.task.file-kinds

    [Fact]
    public void FileKinds_TagsEachDistinctExtensionByTheOwnerMapTestPatterns()
    {
        ArbiterFactSet facts = Enrich(DelegateModelBag(
        [
            "src/Core/Widget.cs",
            "tests/KyberWeave.Tests/WidgetTests.cs",
            "src/web/app.ts",
            "src/web/app.test.ts",
            "src/web/other.test.ts",
        ]));

        ArbiterFact fact = Assert.Single(facts.Facts, pair => pair.Key == "plan.task.file-kinds").Value;
        List<ArbiterFileKind> kinds = Assert.IsAssignableFrom<IEnumerable<ArbiterFileKind>>(fact.Value)
            .OrderBy(kind => kind.Extension, StringComparer.Ordinal)
            .ThenBy(kind => kind.Kind, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                new ArbiterFileKind(".cs", "source"),
                new ArbiterFileKind(".cs", "test"),
                new ArbiterFileKind(".ts", "source"),
                new ArbiterFileKind(".ts", "test"),
            ],
            kinds);
    }

    [Fact]
    public void FileKinds_AbsentWithNoTaskFiles()
    {
        ArbiterFactSet facts = Enrich(DelegateModelBag(taskFiles: null));

        Assert.False(facts.IsPresent("plan.task.file-kinds"));
    }

    // 3. ledger.task.prior-attempt

    [Fact]
    public async Task PriorAttempt_TakesTheProfileFromTheDecisionModelBlockWhenDelivered()
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        DecisionLog decisions = new(ArbiterDirectory);
        await ledger.AppendAsync(Pre("pre-1", "T2", profile: "fast", token: "stale-token"));
        await ledger.AppendAsync(Post("post-1", "pre-1", "FAIL"));
        await decisions.AppendAsync(Decision("dec-1", "pre-1", Block("general", "delivered-token", delivered: true)));

        ArbiterPriorAttempt attempt = PriorAttempt(Enrich(DelegateModelBag(["src/A.cs"])));

        Assert.Equal("fail", attempt.Result);
        Assert.Equal("general", attempt.Profile);
        Assert.Equal("delivered-token", attempt.Token);
    }

    [Fact]
    public async Task PriorAttempt_TakesTheProfileFromThePreEventWhenNotDelivered()
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        DecisionLog decisions = new(ArbiterDirectory);
        await ledger.AppendAsync(Pre("pre-1", "T2", profile: "fast", token: "fast-token"));
        await ledger.AppendAsync(Post("post-1", "pre-1", "PASS"));
        await decisions.AppendAsync(Decision("dec-1", "pre-1", Block("general", "unused-token", delivered: false)));

        ArbiterPriorAttempt attempt = PriorAttempt(Enrich(DelegateModelBag(["src/A.cs"])));

        Assert.Equal("pass", attempt.Result);
        Assert.Equal("fast", attempt.Profile);
        Assert.Equal("fast-token", attempt.Token);
    }

    [Fact]
    public async Task PriorAttempt_UsesTheLatestEarlierPairForTheSameTask()
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        await ledger.AppendAsync(Pre("pre-1", "T2", profile: "fast", token: "t-fast"));
        await ledger.AppendAsync(Post("post-1", "pre-1", "FAIL"));
        await ledger.AppendAsync(Pre("pre-2", "T2", profile: "general", token: "t-general"));
        await ledger.AppendAsync(Post("post-2", "pre-2", "FAIL"));
        await ledger.AppendAsync(Pre("pre-3", "T5", profile: "deep-planning", token: "t-deep"));
        await ledger.AppendAsync(Post("post-3", "pre-3", "PASS"));

        ArbiterPriorAttempt attempt = PriorAttempt(Enrich(DelegateModelBag(["src/A.cs"])));

        Assert.Equal("fail", attempt.Result);
        Assert.Equal("general", attempt.Profile);
        Assert.Equal("t-general", attempt.Token);
    }

    [Fact]
    public async Task PriorAttempt_ABudgetExceededReturnIsAFailureSoTheReworkClimbsARung()
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        await ledger.AppendAsync(Pre("pre-1", "T2", profile: "fast", token: "t-fast"));
        await ledger.AppendAsync(new ArbiterLedgerEvent("post-1", At, "hook", "claude", null, ArbiterLedgerPhases.Post)
        {
            PreId = "pre-1",
            Returns = new ArbiterReturnMarkers(PlannerStatus: "BUDGET_EXCEEDED"),
        });

        ArbiterFactSet facts = Enrich(DelegateModelBag(["src/A.cs"]));

        Assert.Equal("fail", PriorAttempt(facts).Result);
        Assert.True(facts.TryGet("model-targeting.ladder.next", out ArbiterFact? next));
        Assert.Equal("general", next.Value);
    }

    [Fact]
    public void PriorAttempt_AbsentWithNoEarlierAttempt()
    {
        ArbiterFactSet facts = Enrich(DelegateModelBag(["src/A.cs"]));

        Assert.False(facts.IsPresent("ledger.task.prior-attempt"));
    }

    // 4. model-targeting.ladder.next

    [Fact]
    public async Task LadderNext_IsTheConfiguredEntryAfterThePriorProfile()
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        DecisionLog decisions = new(ArbiterDirectory);
        await ledger.AppendAsync(Pre("pre-1", "T2", profile: "fast", token: "t-fast"));
        await ledger.AppendAsync(Post("post-1", "pre-1", "FAIL"));

        ArbiterFactSet facts = Enrich(DelegateModelBag(["src/A.cs"]));

        ArbiterFact next = Assert.Single(facts.Facts, pair => pair.Key == "model-targeting.ladder.next").Value;
        Assert.Equal("general", next.Value);
    }

    [Fact]
    public async Task LadderNext_AbsentWhenThePriorProfileIsTheTop()
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        await ledger.AppendAsync(Pre("pre-1", "T2", profile: "deep-planning", token: "t-deep"));
        await ledger.AppendAsync(Post("post-1", "pre-1", "FAIL"));

        ArbiterFactSet facts = Enrich(DelegateModelBag(["src/A.cs"]));

        Assert.False(facts.IsPresent("model-targeting.ladder.next"));
    }

    [Fact]
    public async Task LadderNext_AbsentWhenThePriorProfileIsUnrecorded()
    {
        InFlightLedger ledger = new(ArbiterDirectory);
        await ledger.AppendAsync(Pre("pre-1", "T2", profile: null, token: null, source: null));
        await ledger.AppendAsync(Post("post-1", "pre-1", "FAIL"));

        ArbiterFactSet facts = Enrich(DelegateModelBag(["src/A.cs"]));

        Assert.False(facts.IsPresent("model-targeting.ladder.next"));
    }

    // 5. delegation.model is the dispatch input's own model field.

    [Fact]
    public void DelegationModel_IsTheDispatchesOwnModelField()
    {
        ArbiterFactSet facts = DelegateModelBag(["src/A.cs"], delegationModel: "claude-haiku-5-5");

        ArbiterFact fact = Assert.Single(facts.Facts, pair => pair.Key == "delegation.model").Value;
        Assert.Equal("claude-haiku-5-5", fact.Value);
        Assert.Equal(ArbiterFactLabel.Asserted, fact.Label);
    }

    [Fact]
    public void DelegationModel_AbsentWhenTheDispatchCarriesNoModel()
    {
        ArbiterFactSet facts = DelegateModelBag(["src/A.cs"], delegationModel: null);

        Assert.False(facts.IsPresent("delegation.model"));
    }
}
