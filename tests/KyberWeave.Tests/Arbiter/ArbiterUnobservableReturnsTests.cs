using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 16.9 (unobservable returns, option (a) of Q17): where a harness
/// cannot observe sub-agent returns, <c>READY-001</c>'s completion check and
/// <c>MODE-001</c>'s RED check answer <c>returns-unobservable</c>, which allows. The
/// in-flight overlap check still escalates. RED: the <c>harness.observes-returns</c>
/// fact, <c>IHarnessHookAdapter.ObservesReturns</c> and the answer do not exist yet.
/// </summary>
public sealed class ArbiterUnobservableReturnsTests
{
    private static readonly string[] NoPaths = [];
    private static readonly string[] SrcA = ["src/a.cs"];
    private static readonly string[] TaskT1 = ["T1"];

    private static ArbiterFactSet Facts(params (string Name, object? Value)[] entries)
    {
        ArbiterFactSet set = new();
        foreach ((string name, object? value) in entries)
        {
            set = set.With(name, value, ArbiterFactLabel.Derived);
        }

        return set;
    }

    private static ArbiterRule Shipped(string id) =>
        ArbiterConfig.ProductDefaults.Rules.Single(rule => rule.Id == id);

    [Fact]
    public void ObservesReturnsFact_IsSuppliedByTheDelegateTriggers()
    {
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("delegate", out IReadOnlySet<string> delegateFacts));
        Assert.Contains("harness.observes-returns", delegateFacts);
        Assert.True(ArbiterTriggerCatalog.TryGetFacts("delegate.returned", out IReadOnlySet<string> returnedFacts));
        Assert.Contains("harness.observes-returns", returnedFacts);
    }

    [Fact]
    public void Antigravity_DeclaresReturnsUnobservable_AndClaudeObservesThem()
    {
        IHookDecisionEngine engine = new NullEngine();

        IHarnessHookAdapter antigravity = new AntigravityHookAdapter(engine);
        IHarnessHookAdapter claude = new ClaudeHookAdapter(engine);

        Assert.False(antigravity.ObservesReturns);
        Assert.True(claude.ObservesReturns);
    }

    [Fact]
    public void Ready001_WhenReturnsUnobservable_DependencyNotComplete_Allows()
    {
        ArbiterRule ready = Shipped("KW-ARB-READY-001");
        ArbiterFactSet facts = Facts(
            ("plan.task.files", SrcA),
            ("plan.task.depends-on", TaskT1),
            ("ledger.completed-tasks", NoPaths),
            ("ledger.in-flight.paths", NoPaths),
            ("harness.observes-returns", false));

        string answer = RuleEngine.Decide(ready, facts);

        Assert.Equal("returns-unobservable", answer);
        Assert.Equal(RuleEffects.Allow, ready.Effects["returns-unobservable"]);
    }

    [Fact]
    public void Ready001_WhenReturnsUnobservable_StillEscalatesInFlightOverlap()
    {
        ArbiterRule ready = Shipped("KW-ARB-READY-001");
        ArbiterFactSet facts = Facts(
            ("plan.task.files", SrcA),
            ("plan.task.depends-on", TaskT1),
            ("ledger.completed-tasks", NoPaths),
            ("ledger.in-flight.paths", SrcA),
            ("harness.observes-returns", false));

        Assert.Equal("overlaps-in-flight", RuleEngine.Decide(ready, facts));
    }

    [Fact]
    public void Ready001_WhenReturnsObserved_DependencyNotComplete_StillWaits()
    {
        ArbiterRule ready = Shipped("KW-ARB-READY-001");
        ArbiterFactSet facts = Facts(
            ("plan.task.files", SrcA),
            ("plan.task.depends-on", TaskT1),
            ("ledger.completed-tasks", NoPaths),
            ("ledger.in-flight.paths", NoPaths),
            ("harness.observes-returns", true));

        Assert.Equal("waiting", RuleEngine.Decide(ready, facts));
    }

    [Fact]
    public void Mode001_WhenReturnsUnobservable_RedMissing_Allows()
    {
        ArbiterRule mode = Shipped("KW-ARB-MODE-001");
        ArbiterFactSet facts = Facts(
            ("plan.development-mode", "test-first"),
            ("delegation.target", "react-dev"),
            ("plan.test-contract.row", "RED: something"),
            ("harness.observes-returns", false));

        string answer = RuleEngine.Decide(mode, facts);

        Assert.Equal("returns-unobservable", answer);
        Assert.Equal(RuleEffects.Allow, mode.Effects["returns-unobservable"]);
    }

    [Fact]
    public void Mode001_WhenReturnsObserved_RedMissing_StillEscalates()
    {
        ArbiterRule mode = Shipped("KW-ARB-MODE-001");
        ArbiterFactSet facts = Facts(
            ("plan.development-mode", "test-first"),
            ("delegation.target", "react-dev"),
            ("plan.test-contract.row", "RED: something"),
            ("harness.observes-returns", true));

        Assert.Equal("missing", RuleEngine.Decide(mode, facts));
    }

    [Fact]
    public void ShippedRules_KeepTheirIds_AndValidateWithZeroErrors()
    {
        var diagnostics = RuleValidator.Validate(ArbiterConfig.ProductDefaults);

        Assert.DoesNotContain(diagnostics, d => d.Severity == KyberWeave.Core.Diagnostics.Severity.Error);
        Assert.Contains(ArbiterConfig.ProductDefaults.Rules, rule => rule.Id == "KW-ARB-READY-001");
        Assert.Contains(ArbiterConfig.ProductDefaults.Rules, rule => rule.Id == "KW-ARB-MODE-001");
    }

    private sealed class NullEngine : IHookDecisionEngine
    {
        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);
    }
}
