using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.1b: the production hook decision engine runs
/// <see cref="ArbiterEvaluator"/> end to end through <c>HookCommand</c> against a
/// temp repo and a fake provider — one deny, one allow, one escalate and one
/// exception-to-block. The scripted-engine host tests stay untouched.
/// RED: <see cref="ArbiterHookDecisionEngine"/> does not exist yet.
/// </summary>
public sealed class ArbiterRealEngineTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _repo.Dispose();
        _home.Dispose();
    }

    private sealed class FakeProvider : IArbiterProvider
    {
        public bool Throw { get; set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new HttpRequestException("provider is down");
            }

            // Provider none semantics: step-1 rules are not evaluated, which is
            // not undecidable, so the family combines step-0 effects alone.
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

    private sealed class ThrowingGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            throw new InvalidOperationException("git facts exploded");
    }

    private static string DisabledShippedRules() =>
        string.Join("\n", ArbiterConfigTests.ExpectedRuleIds.Select(id => $"                - id: {id}\n                  enabled: false"));

    private void WriteConfig(string rulesYaml)
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_repo.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n" +
            "  enabled: true\n" +
            "  provider:\n" +
            "    kind: none\n" +
            "  rules:\n" +
            rulesYaml + "\n");
    }

    private HookCommand CreateCommand(IArbiterProvider provider, IArbiterGitFacts? gitFacts = null)
    {
        ArbiterHookDecisionEngine engine = new(
            providerFactory: _ => provider,
            gitFactsFactory: gitFacts is null ? null : _ => gitFacts,
            homeDirectory: () => _home.Path);
        return new HookCommand(
            HarnessAdapterRegistry.CreateDefault(engine),
            Composition.LoadHostConfig,
            () => "decision-test-1");
    }

    private string Payload(string hookEvent, string target, string prompt, string? status = null)
    {
        string cwd = _repo.Path.Replace("\\", "\\\\", StringComparison.Ordinal);
        string toolInput = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["description"] = "Implement T3",
            ["prompt"] = prompt,
            ["subagent_type"] = target,
        });
        string json = "{\"hook_event_name\":\"" + hookEvent + "\"," +
            "\"session_id\":\"sess-real-1\",\"transcript_path\":\"/tmp/t.jsonl\"," +
            "\"cwd\":\"" + cwd + "\"," +
            "\"permission_mode\":\"default\"," +
            "\"agent_id\":\"agent-conductor-1\",\"agent_type\":\"conductor\"," +
            "\"tool_name\":\"Agent\",\"tool_input\":" + toolInput + "," +
            "\"tool_use_id\":\"toolu-real-1\"";
        if (status is not null)
        {
            json += ",\"tool_response\":{\"status\":\"" + status + "\",\"agentId\":\"agent-7\"," +
                "\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}]}";
        }

        return json + "}";
    }

    private static (int Exit, string Stdout, string Log) Run(HookCommand command, string stdin)
    {
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run("claude", "conductor", stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    private const string Prompt =
        "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement the plan parser.";

    [Fact]
    public void RealEngine_PreDispatch_Deny_WritesDenyShapeWithEscalationEnvelope()
    {
        WriteConfig(
            DisabledShippedRules() + "\n" +
            "                - id: hook-test-deny-001\n" +
            "                  trigger: delegate\n" +
            "                  question: Does this test dispatch always escalate?\n" +
            "                  answers: [escalate-now, within-task]\n" +
            "                  decide:\n" +
            "                    - when: { fact: caller, exists: true }\n" +
            "                      answer: escalate-now\n" +
            "                  effects: { escalate-now: escalate, within-task: allow }");
        HookCommand command = CreateCommand(new FakeProvider());

        (int exit, string stdout, _) = Run(command, Payload("PreToolUse", "csharp-dev", Prompt));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        string reason = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains("STATUS: ARBITER_ESCALATION", reason, StringComparison.Ordinal);
        Assert.Contains("TRIGGER: delegate", reason, StringComparison.Ordinal);
        Assert.Contains("hook-test-deny-001", reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: escalate-now (step 0)", reason, StringComparison.Ordinal);
        Assert.Contains("DECISION_ID: ", reason, StringComparison.Ordinal);
        // The evaluation ran end to end: the ledger and the decision log hold records.
        Assert.True(File.Exists(Path.Combine(_repo.Path, "artifacts", "arbiter", "ledger.jsonl")));
        Assert.True(File.Exists(Path.Combine(_repo.Path, "artifacts", "arbiter", "decisions.jsonl")));
    }

    [Fact]
    public void RealEngine_PreDispatch_Allow_WritesNothing()
    {
        WriteConfig(
            DisabledShippedRules() + "\n" +
            "                - id: hook-test-allow-001\n" +
            "                  trigger: delegate\n" +
            "                  question: Does this test dispatch always allow?\n" +
            "                  answers: [within-task, escalate-now]\n" +
            "                  decide:\n" +
            "                    - when: { fact: caller, exists: true }\n" +
            "                      answer: within-task\n" +
            "                  effects: { within-task: allow, escalate-now: escalate }");
        HookCommand command = CreateCommand(new FakeProvider());

        // docs-dev is not an implementation specialist, so an allow writes nothing.
        (int exit, string stdout, _) = Run(command, Payload("PreToolUse", "docs-dev", Prompt));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void RealEngine_PostDispatch_Escalate_WritesTopLevelBlock()
    {
        WriteConfig(
            DisabledShippedRules() + "\n" +
            "                - id: hook-test-return-001\n" +
            "                  trigger: delegate.returned\n" +
            "                  question: Does this test return always escalate?\n" +
            "                  answers: [adds-work, within-task]\n" +
            "                  decide:\n" +
            "                    - when: { fact: caller, exists: true }\n" +
            "                      answer: adds-work\n" +
            "                  effects: { adds-work: escalate, within-task: allow }");
        HookCommand command = CreateCommand(new FakeProvider());

        (int exit, string stdout, _) = Run(command, Payload("PostToolUse", "csharp-dev", Prompt, "completed"));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        string reason = doc.RootElement.GetProperty("reason").GetString() ?? string.Empty;
        Assert.Contains("STATUS: ARBITER_ESCALATION", reason, StringComparison.Ordinal);
        Assert.Contains("TRIGGER: delegate.returned", reason, StringComparison.Ordinal);
        Assert.Contains("hook-test-return-001", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RealEngine_EvaluatorException_BlocksFailClosedWithHookCode()
    {
        WriteConfig(DisabledShippedRules());
        HookCommand command = CreateCommand(new FakeProvider(), new ThrowingGitFacts());

        (int exit, string stdout, _) = Run(command, Payload("PreToolUse", "csharp-dev", Prompt));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        string reason = doc.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RealEngine_ProviderCrash_BlocksRatherThanPasses()
    {
        WriteConfig(
            DisabledShippedRules() + "\n" +
            "                - id: hook-test-ask-001\n" +
            "                  trigger: delegate\n" +
            "                  question: Does this test ask step 1?\n" +
            "                  answers: [adds-work, within-task]\n" +
            "                  ask:\n" +
            "                    type: choice\n" +
            "                    state: { task: delegation.task }\n" +
            "                    criteria: { adds-work: criterion, within-task: criterion }\n" +
            "                  effects: { adds-work: escalate, within-task: allow }");
        HookCommand command = CreateCommand(new FakeProvider { Throw = true });

        (int exit, string stdout, _) = Run(command, Payload("PreToolUse", "csharp-dev", Prompt));

        // A provider crash degrades to undecidable, which escalates a conductor
        // trigger: the dispatch blocks, never passes.
        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        string reason = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains("STATUS: ARBITER_ESCALATION", reason, StringComparison.Ordinal);
    }
}
