using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 2.7 (RED): delivering the resolved model token to a Claude sub-agent dispatch,
/// as seen by the hook decision engine. Claude's <c>PreToolUse</c> hook takes an <c>updatedInput</c>
/// that replaces the whole tool input (<c>https://code.claude.com/docs/en/hooks</c>), and the Agent
/// tool's <c>model</c> parameter takes an alias: sonnet, opus, haiku or fable
/// (<c>https://code.claude.com/docs/en/sub-agents</c>).
/// RED: <see cref="HookOutcome.ModelToken"/> is never set on an allow, and
/// <c>IContextualHookDecisionEngine.DecidePreDispatch</c> takes no <c>dispatchModel</c> yet.
/// </summary>
/// <remarks>
/// The engine is driven end to end with a fake provider and a plan stub, against a temp repository
/// that holds a model-targets table, so the delivered token is the one the resolver produced.
/// </remarks>
public sealed class ArbiterModelDeliveryTests : IDisposable
{
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";
    private const string DifficultyRule = "KW-ARB-DIFFICULTY-001";
    private const string ScopeRule = "KW-ARB-SCOPE-002";
    private const string ModelTrigger = "delegate.model";

    private static readonly ModelOverrideCapability ClaudeOverride = new(
        true,
        "model",
        new HashSet<string>(StringComparer.Ordinal) { "sonnet", "opus", "haiku", "fable" },
        "https://code.claude.com/docs/en/sub-agents");

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private string ArbiterDirectory => Path.Combine(_repo.Path, "artifacts", "arbiter");

    /// <summary>Answers the primary's scope question as within-task, and the difficulty question as hard, above the floor.</summary>
    private sealed class HardProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = new(StringComparer.Ordinal) { [ScopeRule] = "within-task", [DifficultyRule] = "hard" };
            Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal)
            {
                [DifficultyRule] = new([0.05, 0.05, 0.9], 0.9, 0.8),
            };
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "stub", null, true, scores));
        }
    }

    /// <summary>The plan stands in for a free T2 owned by csharp-dev, standard mode, so the primary allows.</summary>
    private sealed class PlanStub : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            facts
                .With("plan.exists", true, ArbiterFactLabel.Derived)
                .With("plan.tasks.count", 3, ArbiterFactLabel.Derived)
                .With("plan.development-mode", "standard", ArbiterFactLabel.Derived)
                .With("plan.task", "T2", ArbiterFactLabel.Derived)
                .With("plan.task.skills", new List<string> { "csharp-dev" }, ArbiterFactLabel.Derived)
                .With("plan.task.files", new List<string> { "src/A.cs" }, ArbiterFactLabel.Derived)
                .With("plan.task.depends-on", new List<string>(), ArbiterFactLabel.Derived)
                .With("plan.test-contract.row", "A failing test", ArbiterFactLabel.Derived);
    }

    private static KyberWeaveConfig Config(string mode) => new()
    {
        Arbiter = ArbiterConfig.ProductDefaults with
        {
            Enabled = true,
            ModelTargeting = new ArbiterModelTargetingConfig(
                mode,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["easy"] = "fast",
                    ["standard"] = "general",
                    ["hard"] = "deep-planning",
                },
                ["fast", "general", "deep-planning"]),
        },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private ArbiterHookDecisionEngine Engine(IArbiterProvider provider) => new(
        providerFactory: _ => provider,
        planReaderFactory: _ => new PlanStub(),
        homeDirectory: () => _repo.Path);

    private HookContext Context() => new(
        _repo.Path,
        () => "decision-27",
        TextWriter.Null,
        ModelOverride: ClaudeOverride);

    private void WriteTable(string deepToken = "opus")
    {
        Directory.CreateDirectory(ArbiterDirectory);
        ArbiterModelTargets table = new(
            ArbiterModelTargets.SchemaName,
            "claude",
            new ArbiterModelTargetsPreset("work", "d"),
            "b",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["fast"] = "haiku",
                ["general"] = "sonnet",
                ["deep-planning"] = deepToken,
            },
            new Dictionary<string, ArbiterModelTargetsAgent>(StringComparer.Ordinal)
            {
                ["csharp-dev"] = new("general", "sonnet"),
            });
        File.WriteAllText(
            Path.Combine(ArbiterDirectory, "model-targets.claude.json"),
            System.Text.Json.JsonSerializer.Serialize(table));
    }

    private HookOutcome Decide(IArbiterProvider provider, string mode, string? dispatchModel = null) =>
        Engine(provider).DecidePreDispatch(
            "claude",
            "conductor",
            "csharp-dev",
            Prompt,
            Config(mode),
            Context(),
            toolCallId: "tool-27",
            sessionId: "s1",
            dispatchModel: dispatchModel);

    private ArbiterDecisionRecord DelegateModelRecord() =>
        Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), record => record.Trigger == ModelTrigger);

    [Fact]
    public void EnforceWithADeliveringResolver_CarriesTheResolvedTokenOnAllow()
    {
        WriteTable();

        HookOutcome outcome = Decide(new HardProvider(), "enforce");

        Assert.Equal(HookOutcomeKind.Allow, outcome.Kind);
        Assert.Equal("opus", outcome.ModelToken);
    }

    [Fact]
    public void CallerNamedModel_IsHandedToTheDecisionAsDispatchModel_AndTheTokenIsNotDelivered()
    {
        WriteTable();

        HookOutcome outcome = Decide(new HardProvider(), "enforce", dispatchModel: "claude-opus-5-5");

        Assert.Null(outcome.ModelToken);
        ArbiterModelBlock block = Assert.IsType<ArbiterModelBlock>(DelegateModelRecord().Model);
        Assert.False(block.Delivered);
        Assert.Equal("caller-named-model", block.Reason);
    }

    [Fact]
    public void ModeShadow_HookOutcomeCarriesNoToken_ButTheDecisionStillHoldsTheModelBlock()
    {
        WriteTable();

        HookOutcome outcome = Decide(new HardProvider(), "shadow");

        Assert.Equal(HookOutcomeKind.Allow, outcome.Kind);
        Assert.Null(outcome.Reason);
        Assert.Null(outcome.ModelToken);
        ArbiterModelBlock block = Assert.IsType<ArbiterModelBlock>(DelegateModelRecord().Model);
        Assert.False(block.Delivered);
        Assert.Equal("shadow", block.Reason);
    }

    // Delivered truthfulness: the decision says delivered only for a token the adapter writes.

    [Fact]
    public void ATokenTheHarnessDoesNotAccept_IsNotRecordedDelivered()
    {
        // t-deep is not an alias the Agent tool accepts, so no adapter writes it. The decision must not claim it was delivered.
        WriteTable("t-deep");

        HookOutcome outcome = Decide(new HardProvider(), "enforce");

        Assert.Null(outcome.ModelToken);
        ArbiterModelBlock block = Assert.IsType<ArbiterModelBlock>(DelegateModelRecord().Model);
        Assert.False(block.Delivered);
        Assert.Equal("token-not-accepted", block.Reason);
    }

    [Fact]
    public void AnAcceptedAlias_UnderEnforce_IsRecordedDelivered()
    {
        WriteTable("haiku");

        HookOutcome outcome = Decide(new HardProvider(), "enforce");

        Assert.Equal("haiku", outcome.ModelToken);
        ArbiterModelBlock block = Assert.IsType<ArbiterModelBlock>(DelegateModelRecord().Model);
        Assert.True(block.Delivered);
        Assert.Null(block.Reason);
    }

    [Fact]
    public void AWriteThatFails_ThroughTheClaudeAdapter_IsNotRecordedDelivered()
    {
        // The engine decides before the adapter writes. When the adapter's write fails the dispatch runs without
        // the model, so the decision must not say the model was delivered.
        WriteTable("haiku");
        ClaudeHookAdapter adapter = new(Engine(new HardProvider())) { ModelWriteFault = new IOException("write refused") };
        string raw = AgentDispatchPayload();
        using JsonDocument doc = JsonDocument.Parse(raw);

        _ = adapter.Handle(doc.RootElement, raw, "conductor", Config("enforce"), Context());

        ArbiterModelBlock block = Assert.IsType<ArbiterModelBlock>(DelegateModelRecord().Model);
        Assert.False(block.Delivered);
    }

    private static string AgentDispatchPayload()
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "claude", "pre-agent-dispatch.json")))
            ?? throw new InvalidOperationException("The shipped Claude fixture is not a JSON document.");
        root["tool_input"] = new JsonObject
        {
            ["subagent_type"] = "csharp-dev",
            ["prompt"] = Prompt,
            ["description"] = "Implement T2",
        };
        return root.ToJsonString();
    }
}
