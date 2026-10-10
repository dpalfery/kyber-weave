using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
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
/// <remarks>
/// The hand-fed rules-engine tests below pin the shipped rules' answers; the wiring
/// tests at the end drive the real <see cref="ArbiterHookDecisionEngine"/> through the
/// real harness adapters (review 20.1, Major 1): the fact was declared but never
/// supplied, so the shipped skip never fired on the only harness that needs it.
/// </remarks>
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
    public void ObservesReturnsFact_IsDeclaredByTheDelegateTriggers()
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

    // ---------------------------------------------------------------------
    // Wiring (review 20.1, Major 1): the real engine, real adapters, real
    // shipped rules and real plan reader decide a dependent dispatch.
    // ---------------------------------------------------------------------

    private sealed class WiringRepo : IDisposable
    {
        public TempDirectory Repo { get; } = new();

        public string Path => Repo.Path;

        public void Dispose() => Repo.Dispose();
    }

    private sealed class WiringProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                if (rule.Ask is not null)
                {
                    answers[rule.Id] = ArbiterStep1BatchResult.NotEvaluated;
                }
            }

            return Task.FromResult(new ArbiterStep1BatchResult(answers, null, null, false));
        }
    }

    private static string[] NonReadyRuleIds =>
        ArbiterConfigTests.ExpectedRuleIds.Where(id => id != "KW-ARB-READY-001").ToArray();

    private static string ReadyOnlyRules() =>
        string.Join(
            "\n",
            NonReadyRuleIds.Select(id => $"                - id: {id}\n                  enabled: false")
                .Append("                - id: KW-ARB-READY-001"));

    private static string DispatchPrompt =>
        "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement the parser.";

    private static HookCommand RealCommand(WiringRepo repo, string home)
    {
        ArbiterHookDecisionEngine engine = new(
            providerFactory: _ => new WiringProvider(),
            homeDirectory: () => home);
        return new HookCommand(
            HarnessAdapterRegistry.CreateDefault(engine),
            Composition.LoadHostConfig,
            () => "wiring-decision-1");
    }

    private static string AntigravityPayload(string repoRoot, string prompt)
    {
        string escapedRoot = repoRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        return "{" +
            "\"hook_event_name\":\"PreToolUse\"," +
            "\"conversationId\":\"conv-wiring-1\"," +
            "\"stepIdx\":0," +
            "\"workspacePaths\":[\"" + escapedRoot + "\"]," +
            "\"toolCall\":{" +
            "\"name\":\"invoke_subagent\"," +
            "\"args\":{" +
            "\"Subagents\":[{\"TypeName\":\"csharp-dev\",\"Prompt\":" +
            JsonSerializer.Serialize(prompt) + "}]}}}";
    }

    private static string ClaudePayload(string repoRoot, string prompt)
    {
        string escapedRoot = repoRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
        string toolInput = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["description"] = "Implement T3",
            ["prompt"] = prompt,
            ["subagent_type"] = "csharp-dev",
        });
        return "{\"hook_event_name\":\"PreToolUse\"," +
            "\"session_id\":\"sess-wiring-1\",\"cwd\":\"" + escapedRoot + "\"," +
            "\"agent_id\":\"agent-wiring-1\",\"agent_type\":\"conductor\"," +
            "\"tool_name\":\"Agent\",\"tool_input\":" + toolInput + "," +
            "\"tool_use_id\":\"toolu-wiring-1\"}";
    }

    [Fact]
    public void Antigravity_DependentDispatchWithoutLedgerCompletion_Allows_AndLogsTheSkip()
    {
        using WiringRepo repo = new();
        using TempDirectory home = new();
        WriteReadyOnlyConfig(repo.Path);
        WritePlanWithDependency(repo.Path);
        HookCommand command = RealCommand(repo, home.Path);

        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run(
            "antigravity", null, AntigravityPayload(repo.Path, DispatchPrompt), stdout, log);

        Assert.Equal(0, exit);
        // Antigravity's documented allow document: the empty object, never 'allow'.
        Assert.Equal("{}", stdout.ToString().Trim());
        // The skip is logged: the decision record carries the shipped answer.
        string decisions = File.ReadAllText(System.IO.Path.Combine(repo.Path, "artifacts", "arbiter", "decisions.jsonl"));
        Assert.Contains("KW-ARB-READY-001", decisions, StringComparison.Ordinal);
        Assert.Contains("returns-unobservable", decisions, StringComparison.Ordinal);
    }

    [Fact]
    public void Claude_SameDependentDispatchWithoutLedgerCompletion_StillEscalates()
    {
        using WiringRepo repo = new();
        using TempDirectory home = new();
        WriteReadyOnlyConfig(repo.Path);
        WritePlanWithDependency(repo.Path);
        HookCommand command = RealCommand(repo, home.Path);

        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run(
            "claude", "conductor", ClaudePayload(repo.Path, DispatchPrompt), stdout, log);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        string reason = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains("STATUS: ARBITER_ESCALATION", reason, StringComparison.Ordinal);
        Assert.Contains("waiting", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FactBuilder_SuppliesObservesReturns_AsDerivedFact_FromTheEngine()
    {
        TriggerClassification classification = Classify("delegate", "csharp-dev", DispatchPrompt);
        ArbiterFactSet observed = TriggerFactBuilder.Build(classification, DispatchPrompt, DocsConfig(), observesReturns: true);
        ArbiterFactSet unobserved = TriggerFactBuilder.Build(classification, DispatchPrompt, DocsConfig(), observesReturns: false);

        Assert.True(observed.TryGet("harness.observes-returns", out ArbiterFact? observedFact));
        Assert.Equal(true, observedFact!.Value);
        Assert.Equal(ArbiterFactLabel.Derived, observedFact.Label);
        Assert.True(unobserved.TryGet("harness.observes-returns", out ArbiterFact? unobservedFact));
        Assert.Equal(false, unobservedFact!.Value);
        Assert.Equal(ArbiterFactLabel.Derived, unobservedFact.Label);
    }

    private static TriggerClassification Classify(string trigger, string? target, string prompt)
    {
        _ = trigger;
        ArbiterEvent ev = new() { Harness = "antigravity", Caller = null, Target = target, Prompt = prompt };
        return TriggerClassifier.ClassifySingle(ev, target, prompt);
    }

    private static KyberWeaveConfig DocsConfig() => new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static void WriteReadyOnlyConfig(string repoRoot)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(repoRoot, ".kyber-weave"));
        File.WriteAllText(
            System.IO.Path.Combine(repoRoot, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n" +
            "  enabled: true\n" +
            "  provider:\n" +
            "    kind: none\n" +
            "  rules:\n" +
            ReadyOnlyRules() + "\n");
    }

    private static void WritePlanWithDependency(string repoRoot)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(repoRoot, "docs", "plans"));
        File.WriteAllText(
            System.IO.Path.Combine(repoRoot, "docs", "plans", "plan.md"),
            "# Wiring plan\n\n" +
            "## Tasks\n\n" +
            "### T1 — First task\n" +
            "Files: src/one.cs\n\n" +
            "### T3 — Dependent task\n" +
            "Files: src/a.cs\n" +
            "Depends on: T1\n" +
            "Required skills: csharp-dev\n");
    }
}
