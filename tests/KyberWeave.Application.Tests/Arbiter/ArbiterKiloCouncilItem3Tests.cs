using KyberWeave.Contracts.Repositories;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Council item 3 re-investigation (task 3.19-F2, part B). The council claimed the same-failure advisory drops the
/// resolved model token. The first RED for it was not clean: the repeat rework was denied with <c>ARBITER_ESCALATION</c>
/// before the same-failure branch ran. This file isolates that escalation and removes it, so the repeat reaches the
/// advisory with model targeting enforced.
/// </summary>
/// <remarks>
/// Cause of the escalation, found by running the real engine: rule <c>KW-ARB-READY-001</c> answered
/// <c>overlaps-in-flight</c> on the repeat. The first rework's pre-dispatch entry was still open in the in-flight
/// ledger, because nothing closed it. A worker's return closes it through the post-dispatch path, so the fixture now
/// posts the first failure's return before the repeat. Nothing else in the fixture changes.
/// </remarks>
public sealed class ArbiterKiloCouncilItem3Tests : IDisposable
{
    private const string Rework =
        "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\n" +
        "FAILURE-CLUSTER: cluster-a\n\nFAILURE:\nbuild broke at Widget.cs:12\n\nImplement T2 again.";
    private const string FirstReturn = "FAILURE: build broke at Widget.cs:12";
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string DifficultyRule = "KW-ARB-DIFFICULTY-001";
    private const string FailureRule = "KW-ARB-FAILURE-001";

    private static readonly ModelOverrideCapability ClaudeOverride = new(
        true,
        "model",
        new HashSet<string>(StringComparer.Ordinal) { "sonnet", "opus", "haiku", "fable" },
        "https://code.claude.com/docs/en/sub-agents");

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// The plan facts the evaluator reads, with the rating the task file records: under the one remaining roster
    /// the difficulty rule takes that rating as its answer and asks no model.
    /// </summary>
    private sealed class PlanStub : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
                .With("plan.task.difficulty", "hard", ArbiterFactLabel.Derived)
                .With("plan.tasks.count", 3, ArbiterFactLabel.Derived)
                .With("plan.development-mode", "standard", ArbiterFactLabel.Derived)
                .With("plan.task", "T2", ArbiterFactLabel.Derived)
                .With("plan.task.skills", new List<string> { "dev-medium" }, ArbiterFactLabel.Derived)
                .With("plan.task.files", new List<string> { "src/A.cs" }, ArbiterFactLabel.Derived)
                .With("plan.task.depends-on", new List<string>(), ArbiterFactLabel.Derived)
                .With("plan.test-contract.row", "A failing test", ArbiterFactLabel.Derived);
    }

    private static KyberWeaveConfig EnforcedConfig() => new()
    {
        Arbiter = ArbiterConfigDefaults.Product with
        {
            Enabled = true,
            Rules = ArbiterConfigDefaults.Product.Rules
                .Select(rule => string.Equals(rule.Id, FailureRule, StringComparison.Ordinal) ? rule with { Shadow = false } : rule)
                .ToList(),
            ModelTargeting = new ArbiterModelTargetingConfig(
                "enforce",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["easy"] = "fast",
                    ["standard"] = "general",
                    ["hard"] = "dev-hard",
                },
                ["fast", "general", "dev-hard"]),
        },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private ArbiterHookDecisionEngine Engine(IArbiterProvider provider) => _host.Engine(
        providerFactory: _ => provider,
        gitFactsFactory: _ => new NoGitFacts(),
        planReaderFactory: _ => new PlanStub(),
        homeDirectory: () => _host.RepoRoot);

    private sealed class NoGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    private HookContext Context() => new(
        _host.RepoRoot,
        () => "decision-27",
        TextWriter.Null,
        ModelOverride: ClaudeOverride);

    private void WriteTable()
    {
        ArbiterModelTargets table = new(
            ArbiterModelTargets.SchemaName,
            "claude",
            new ArbiterModelTargetsPreset("work", "d"),
            "b",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["fast"] = "haiku",
                ["general"] = "sonnet",
                ["dev-hard"] = "opus",
            },
            new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal)
            {
                ["dev-medium"] = new("general", "sonnet"),
            });
        _ = _host.Write(
            Path.Combine(_host.ArbiterDirectory, "model-targets.claude.json"),
            JsonSerializer.Serialize(table));
    }

    /// <summary>
    /// A repeated rework that the same-failure advisory annotates still carries the model its targeting resolved, with
    /// the advisory's note beside it. Failure this guards: the advisory branch returns before the model is resolved, so
    /// the repeated attempt runs on the wrong model with no record of why.
    /// </summary>
    [Fact]
    public async Task ASameFailureAdvisoryKeepsTheResolvedModelOnTheRework()
    {
        WriteTable();
        FakeArbiterProvider provider = new FakeArbiterProvider
        {
            Answers = { [ScopeRule] = "within-task", [DifficultyRule] = "hard", [FailureRule] = "same" },
            Scores = { [DifficultyRule] = new([0.05, 0.05, 0.9], 0.9, 0.8), [FailureRule] = new(null, 0.9, 0.85) },
            Model = "stub",
        };
        KyberWeaveConfig config = EnforcedConfig();

        HookOutcome first = await Engine(provider).DecidePreDispatchAsync(
            "claude", "conductor", "dev-medium", Rework, config, Context(),
            toolCallId: "tool-27", sessionId: "s1", dispatchModel: null);
        Assert.True(
            first.Kind == HookOutcomeKind.Allow,
            $"expected the first rework to be allowed, got {first.Kind}: {first.Reason}");

        // The worker returned, which closes the first rework's in-flight entry; without this the repeat overlaps it.
        await Engine(provider).DecidePostDispatchAsync(
            "claude", "conductor", "dev-medium", Rework, FirstReturn, config, Context(),
            toolCallId: "tool-27", sessionId: "s1");

        HookOutcome repeat = await Engine(provider).DecidePreDispatchAsync(
            "claude", "conductor", "dev-medium", Rework, config, Context(),
            toolCallId: "tool-28", sessionId: "s1", dispatchModel: null);

        Assert.True(
            repeat.Kind == HookOutcomeKind.AllowWithNote,
            $"expected AllowWithNote for the repeated rework, got {repeat.Kind}: {repeat.Reason}");
        Assert.StartsWith("ARBITER_SAME_FAILURE: yes", repeat.Reason, StringComparison.Ordinal);
        Assert.Equal("opus", repeat.ModelToken);
        Assert.NotNull(repeat.ModelBlock);
    }
}
