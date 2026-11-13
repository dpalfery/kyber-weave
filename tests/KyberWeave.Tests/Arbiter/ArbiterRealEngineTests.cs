using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.1b: the production hook decision engine runs
/// <see cref="ArbiterEvaluator"/> end to end through <c>HookCommand</c> against a
/// temp repo and a fake provider — one deny, one allow, one escalate and one
/// exception-to-allow-loudly. A hook or decision-model failure writes to the log
/// and the chat-visible note and lets the dispatch continue; it never blocks.
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

        /// <summary>Rule id to the choice answered for it with confidence <see cref="Confidence"/>; others are not evaluated.</summary>
        public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);

        public double Confidence { get; set; } = 0.95;

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
            Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                if (rule.Ask is null)
                {
                    continue;
                }

                if (Answers.TryGetValue(rule.Id, out string? answer))
                {
                    answers[rule.Id] = Confidence >= (rule.Ask.ConfidenceAtLeast ?? 0) ? answer : RuleEngine.Undecidable;
                    scores[rule.Id] = new ArbiterStep1Score(null, Confidence, rule.Ask.ConfidenceAtLeast);
                }
                else
                {
                    answers[rule.Id] = ArbiterStep1BatchResult.NotEvaluated;
                }
            }

            return Task.FromResult(new ArbiterStep1BatchResult(answers, null, null, Answers.Count > 0, scores));
        }
    }

    private sealed class ThrowingGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            throw new InvalidOperationException("git facts exploded");
    }

    /// <summary>
    /// Disables every catalogued rule except the lens-skip rule. That rule only answers
    /// <c>lens.spawn</c>, which these tests never send, so leaving it enabled changes nothing here.
    /// </summary>
    private static string DisabledShippedRules() =>
        string.Join(
            "\n",
            ArbiterConfigTests.ExpectedRuleIds
                .Where(id => !string.Equals(id, LensNoCodeChangeRule, StringComparison.Ordinal))
                .Select(id => $"                - id: {id}\n                  enabled: false"));

    private void WriteConfig(string rulesYaml, string providerYaml = "    kind: none")
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_repo.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n" +
            "  enabled: true\n" +
            "  provider:\n" +
            providerYaml + "\n" +
            "  rules:\n" +
            rulesYaml + "\n");
    }

    private HookCommand CreateCommand(
        IArbiterProvider provider,
        IArbiterGitFacts? gitFacts = null,
        Func<ICredentialStore>? credentialStoreFactory = null,
        Func<string, string?>? environment = null)
    {
        ArbiterHookDecisionEngine engine = new(
            providerFactory: _ => provider,
            gitFactsFactory: gitFacts is null ? null : _ => gitFacts,
            homeDirectory: () => _home.Path,
            credentialStoreFactory: credentialStoreFactory,
            environment: environment);
        return new HookCommand(
            HarnessAdapterRegistry.CreateDefault(engine),
            Composition.LoadHostConfig,
            () => "decision-test-1");
    }

    private string Payload(string hookEvent, string target, string prompt, string? status = null, string agentType = "conductor")
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
            "\"agent_id\":\"agent-" + agentType + "-1\",\"agent_type\":\"" + agentType + "\"," +
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

    private const string LensNoCodeChangeRule = "KW-ARB-LENS-002";

    private const string LensPrompt =
        "KYBER-ARBITER: true\nLENS: correctness\n\nApply the lens to the change.";

    /// <summary>Adds a Markdown-only review's git facts to the facts the builder produced.</summary>
    private sealed class MarkdownOnlyGitFacts : IArbiterGitFacts
    {
        private static readonly string[] MarkdownPaths = ["docs/guide.md"];

        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) =>
            facts.With("review.changed-paths", MarkdownPaths, ArbiterFactLabel.Asserted)
                .With("review.no-code-change-paths", MarkdownPaths, ArbiterFactLabel.Asserted)
                .With("review.changed-hunks", "hunks", ArbiterFactLabel.Derived);
    }

    [Fact]
    public void RealEngine_LensSpawnOnAMarkdownOnlyChange_SkipsWithAReviewNoteNamingRuleAndReason()
    {
        WriteConfig(DisabledShippedRules());
        HookCommand command = CreateCommand(new FakeProvider(), new MarkdownOnlyGitFacts());

        (int exit, string stdout, _) = Run(
            command,
            Payload("PreToolUse", "review-lens", LensPrompt, agentType: "code-reviewer"));

        Assert.Equal(0, exit);
        Assert.Contains("KW-ARB-LENS-002", stdout, StringComparison.Ordinal);
        Assert.Contains("no-code-change", stdout, StringComparison.Ordinal);
    }

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
    public void RealEngine_EvaluatorException_AllowsLoudlyWithHookCode()
    {
        WriteConfig(DisabledShippedRules());
        HookCommand command = CreateCommand(new FakeProvider(), new ThrowingGitFacts());

        (int exit, string stdout, string log) = Run(command, Payload("PreToolUse", "csharp-dev", Prompt));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        string note = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, note, StringComparison.Ordinal);
        Assert.Contains("git facts exploded", note, StringComparison.Ordinal);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A loud allow to an implementation specialist still strips the routing headers: the
    /// worker gets a clean prompt plus the note, never the headers the strip exists to
    /// remove.
    /// </summary>
    [Fact]
    public void RealEngine_LoudAllowToASpecialistStillStripsHeaders()
    {
        WriteConfig(DisabledShippedRules());
        HookCommand command = CreateCommand(new FakeProvider(), new ThrowingGitFacts());

        (int exit, string stdout, _) = Run(command, Payload("PreToolUse", "csharp-dev", Prompt));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        string updated = output.GetProperty("updatedInput").GetProperty("prompt").GetString() ?? string.Empty;
        Assert.DoesNotContain("KYBER-ARBITER", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("TASK: T3", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void RealEngine_RemoteProviderWithoutSecretTool_AllowsLoudlyWithActionableEvidence()
    {
        WriteConfig(DisabledShippedRules(), RemoteProviderYaml);
        HookCommand command = CreateCommand(
            new FakeProvider(),
            credentialStoreFactory: () => new SecretServiceCredentialStore(
                new ArbiterSecretToolMissingTests.MissingExecutableCredentialProcessRunner()),
            environment: _ => null);

        (int exit, string stdout, string log) = Run(command, Payload("PreToolUse", "csharp-dev", Prompt));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        string note = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, note, StringComparison.Ordinal);
        Assert.Contains("TYPESAFE_API_KEY", note, StringComparison.Ordinal);
        Assert.Contains("libsecret-tools", note, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred trying to start process", note, StringComparison.Ordinal);
        Assert.Contains("TYPESAFE_API_KEY", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RealEngine_RemoteProviderWithRealSecretServiceAndNoSecretTool_AllowsLoudlyWithActionableEvidence()
    {
        Assert.SkipWhen(SecretToolOnPath(), "secret-tool is installed, so the missing-tool path is not reachable here.");
        WriteConfig(DisabledShippedRules(), RemoteProviderYaml);
        HookCommand command = CreateCommand(
            new FakeProvider(),
            credentialStoreFactory: () => new SecretServiceCredentialStore(new ProcessRunnerCredentialProcessRunner()),
            environment: _ => null);

        (int exit, string stdout, string log) = Run(command, Payload("PreToolUse", "csharp-dev", Prompt));

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        string note = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains("TYPESAFE_API_KEY", note, StringComparison.Ordinal);
        Assert.Contains("libsecret-tools", note, StringComparison.Ordinal);
        Assert.Contains("TYPESAFE_API_KEY", log, StringComparison.Ordinal);
    }

    private const string RemoteProviderYaml =
        "    kind: systemone\n" +
        "    endpoint: https://api.typesafe.ai/v1\n" +
        "    model: jev-1.13.0";

    private static bool SecretToolOnPath()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, "secret-tool")));
    }

    [Fact]
    public void RealEngine_ProviderCrash_AllowsLoudlyRatherThanBlocks()
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

        (int exit, string stdout, string log) = Run(command, Payload("PreToolUse", "csharp-dev", Prompt));

        // A provider crash leaves the question undecidable, which allows; the failure is logged, never a block.
        Assert.Equal(0, exit);
        Assert.DoesNotContain("\"deny\"", stdout, StringComparison.Ordinal);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
        Assert.Contains("the decision model failed", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs one factory <c>PreToolUse</c> <c>Task</c> dispatch to <c>architect</c> through the
    /// production hook command, with the shipped rules active and a <c>kind: none</c> provider.
    /// </summary>
    private (int Exit, string Stdout, string Log) RunFactory(string prompt, string? sessionId, FakeProvider? provider = null)
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_repo.Path, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n" +
            "  docs-root: docs\n" +
            "arbiter:\n" +
            "  enabled: true\n" +
            "  provider:\n" +
            "    kind: none\n");

        string cwd = _repo.Path.Replace("\\", "\\\\", StringComparison.Ordinal);
        string toolInput = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["description"] = "Write the plan",
            ["prompt"] = prompt,
            ["subagent_type"] = "architect",
        });
        string session = sessionId is null ? string.Empty : "\"session_id\":\"" + sessionId + "\",";
        string stdin = "{\"hook_event_name\":\"PreToolUse\"," + session +
            "\"cwd\":\"" + cwd + "\",\"tool_name\":\"Task\",\"tool_input\":" + toolInput + "}";

        HookCommand command = CreateCommand(provider ?? new FakeProvider());
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run("factory", null, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    private static string DenyReason(string stdout)
    {
        using JsonDocument doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("hookSpecificOutput")
            .GetProperty("permissionDecisionReason")
            .GetString() ?? string.Empty;
    }

    [Fact]
    public void RealEngine_Factory_AbsolutePlanFile_NoLongerBlocksByItself()
    {
        // ADR 0072: an absolute PLAN_FILE is a spelling, not a finding. Step 0 cannot recognise it, the model is
        // asked, and with no model the dispatch proceeds.
        (int exit, string stdout, _) = RunFactory(
            "KYBER-ARBITER: true\nPLAN_FILE: /Users/x/repo.worktrees/fix/docs/plans/plan.md\n\nWrite the plan.",
            "sess-factory-absolute");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void RealEngine_Factory_ConfidentMalformedAnswer_DeniesWithTheModelReason()
    {
        FakeProvider provider = new();
        provider.Answers["KW-ARB-PLANNER-001"] = "malformed";

        (int exit, string stdout, _) = RunFactory(
            "KYBER-ARBITER: true\nPLAN_FILE: /Users/x/repo.worktrees/fix/docs/plans/plan.md\n\nRewrite src/Program.cs.",
            "sess-factory-absolute",
            provider);

        Assert.Equal(0, exit);
        string reason = DenyReason(stdout);
        Assert.Contains("ANSWER: malformed (step 1)", reason, StringComparison.Ordinal);
        Assert.Contains("CALLER: conductor (factory)", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RealEngine_Factory_LowConfidenceMalformedAnswer_Allows()
    {
        FakeProvider provider = new() { Confidence = 0.4 };
        provider.Answers["KW-ARB-PLANNER-001"] = "malformed";

        (int exit, string stdout, _) = RunFactory(
            "KYBER-ARBITER: true\nPLAN_FILE: /Users/x/repo.worktrees/fix/docs/plans/plan.md\n\nWrite the plan.",
            "sess-factory-absolute",
            provider);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void RealEngine_Factory_RelativePlanFile_Allows()
    {
        (int exit, string stdout, _) = RunFactory(
            "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nWrite the plan.",
            "sess-factory-relative");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Theory]
    [InlineData("INTAKE: docs/todo/item.md")]
    [InlineData("FINDINGS:")]
    [InlineData("STATUS: ARBITER_ESCALATION")]
    [InlineData("FEATURE: search\nPHASE: design")]
    public void RealEngine_Factory_MarkerOnlyPlannerDispatch_Allows(string body)
    {
        (int exit, string stdout, _) = RunFactory($"KYBER-ARBITER: true\n\n{body}", "sess-factory-marker");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void RealEngine_Factory_UnmarkedPlannerDispatch_PassesThrough()
    {
        (int exit, string stdout, _) = RunFactory("Plan the upgrade.", "sess-factory-unmarked");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void RealEngine_Factory_RepeatWithoutPlanFile_CountsOneSession()
    {
        const string WithoutPlanFile = "KYBER-ARBITER: true\n\nWrite the plan.";

        string first = DenyReason(RunFactory(WithoutPlanFile, "sess-A", Malformed()).Stdout);
        string second = DenyReason(RunFactory(WithoutPlanFile, "sess-A", Malformed()).Stdout);
        string otherSession = DenyReason(RunFactory(WithoutPlanFile, "sess-B", Malformed()).Stdout);

        Assert.Contains("REPEAT: 1", first, StringComparison.Ordinal);
        Assert.Contains("REPEAT: 2", second, StringComparison.Ordinal);
        Assert.Contains("REPEAT: 1", otherSession, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RealEngine_Factory_RepeatWithoutPlanFileOrSession_NeverCountsTogether(string? sessionId)
    {
        const string WithoutPlanFile = "KYBER-ARBITER: true\n\nWrite the plan.";

        string first = DenyReason(RunFactory(WithoutPlanFile, sessionId, Malformed()).Stdout);
        string second = DenyReason(RunFactory(WithoutPlanFile, sessionId, Malformed()).Stdout);

        Assert.Contains("REPEAT: 1", first, StringComparison.Ordinal);
        Assert.Contains("REPEAT: 1", second, StringComparison.Ordinal);
    }

    /// <summary>A provider that confidently answers the planner rule's violating choice, so the dispatch escalates.</summary>
    private static FakeProvider Malformed()
    {
        FakeProvider provider = new();
        provider.Answers["KW-ARB-PLANNER-001"] = "malformed";
        return provider;
    }
}
