using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 16.5: the Factory harness adapter. Covers the dispatch
/// gate on <c>PreToolUse</c> with <c>tool_name == "Task"</c>, the target from
/// <c>tool_input.subagent_type</c> and the prompt from <c>tool_input.prompt</c>,
/// pairing by <c>pair-digest</c> (Factory documents no call id, so the adapter
/// passes a null call id and the ledger pairs the return by digest), the
/// Claude-format answer shapes (<c>hookSpecificOutput.permissionDecision</c>
/// deny with <c>permissionDecisionReason</c>, <c>allow</c> with the complete
/// <c>updatedInput</c>, a plain pass writing nothing), post-dispatch outcomes
/// delivered as <c>hookSpecificOutput.additionalContext</c>, and the internal
/// error writing the deny shape.
/// Fixture payloads live under <c>Fixtures/arbiter-hooks/factory/</c>, built
/// from [F11] of the task packet.
/// RED: <c>FactoryHookAdapter</c> does not exist yet.
/// </summary>
public sealed class FactoryHookAdapterTests
{
    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public string? SeenPreTarget;

        public string? SeenPrePrompt;

        public bool ThrowOnPre;

        public bool ThrowOnPost;

        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config)
        {
            SeenPreTarget = target;
            SeenPrePrompt = prompt;
            if (ThrowOnPre)
            {
                throw new InvalidOperationException("provider is down");
            }

            return PreResult;
        }

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            if (ThrowOnPost)
            {
                throw new InvalidOperationException("provider is down");
            }

            return PostResult;
        }
    }

    private sealed class ContextualEngine : IContextualHookDecisionEngine
    {
        public string? SeenPreHarness;

        public string? SeenPreTarget;

        public string? SeenPrePrompt;

        public string? SeenPreToolCallId;

        public string? SeenPreSessionId;

        public string? SeenPostToolCallId;

        public string? SeenPostSessionId;

        public int PostCalls;

        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            PreResult;

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            PostResult;

        public HookOutcome DecidePreDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null)
        {
            SeenPreHarness = harness;
            SeenPreTarget = target;
            SeenPrePrompt = prompt;
            SeenPreToolCallId = toolCallId;
            SeenPreSessionId = sessionId;
            return PreResult;
        }

        public HookOutcome DecidePostDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null)
        {
            PostCalls++;
            SeenPostToolCallId = toolCallId;
            SeenPostSessionId = sessionId;
            return PostResult;
        }
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "factory", name));

    private static (int Exit, string Stdout, string Log) Run(
        string stdin,
        string? caller,
        IHookDecisionEngine engine,
        Func<string, KyberWeaveConfig>? loader = null)
    {
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            loader ?? (_ => EnabledConfig()),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run(FactoryHookAdapter.Token, caller, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    [Fact]
    public void PreDispatch_Deny_WritesDenyShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", specific.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", specific.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, specific.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void PreDispatch_Allow_SpecialistTarget_StripsCompleteInput()
    {
        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", specific.GetProperty("permissionDecision").GetString());

        // updatedInput is the complete tool_input: every original field survives.
        JsonElement updated = specific.GetProperty("updatedInput");
        Assert.Equal("csharp-dev", updated.GetProperty("subagent_type").GetString());
        Assert.Equal("Implement T16.5", updated.GetProperty("description").GetString());
        string prompt = updated.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Implement the adapter.", prompt);
        Assert.DoesNotContain("KYBER-ARBITER", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_Allow_NonSpecialistTarget_WritesNothing()
    {
        string stdin = Fixture("pre-task-dispatch.json").Replace(
            "\"subagent_type\": \"csharp-dev\"", "\"subagent_type\": \"docs-dev\"", StringComparison.Ordinal);

        (int exit, string stdout, _) = Run(stdin, null, new ScriptedEngine());

        Assert.Equal(0, exit);

        // A plain pass writes nothing: an explicit allow would auto-approve the
        // call, and the empty stdout on exit 0 is the documented proceed.
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void PreDispatch_AllowWithRewrite_WritesRewrittenInput()
    {
        const string rewritten = """{"prompt":"Hello.","subagent_type":"csharp-dev"}""";
        ScriptedEngine engine = new()
        {
            PreResult = new HookOutcome(HookOutcomeKind.AllowWithRewrite, RewrittenInputJson: rewritten),
        };

        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement updated = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.Equal("Hello.", updated.GetProperty("prompt").GetString());
    }

    [Fact]
    public void PreDispatch_TargetAndPrompt_ComeFromSubagentTypeAndPrompt()
    {
        ContextualEngine engine = new();

        (int exit, _, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(FactoryHookAdapter.Token, engine.SeenPreHarness);
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
        Assert.StartsWith("KYBER-ARBITER: true", engine.SeenPrePrompt, StringComparison.Ordinal);
        Assert.Contains("Implement the adapter.", engine.SeenPrePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_MissingTarget_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ContextualEngine engine = new();
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // An event with no sub-agent target passes through untouched (design 1.7):
        // empty stdout, exit 0, and no configuration load.
        int exit = command.Run(FactoryHookAdapter.Token, null, Fixture("pre-task-no-target.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
        Assert.Null(engine.SeenPreTarget);
    }

    [Fact]
    public void PreDispatch_NonDispatchTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ContextualEngine engine = new();
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // Plan reads stay advisory on Factory: the Read guard does not apply, and
        // a non-Task tool passes through before configuration loads.
        int exit = command.Run(FactoryHookAdapter.Token, null, Fixture("pre-non-dispatch.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void PreDispatch_LowercaseTaskTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ContextualEngine engine = new();
        string stdin = Fixture("pre-task-dispatch.json").Replace(
            "\"tool_name\": \"Task\"", "\"tool_name\": \"task\"", StringComparison.Ordinal);
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // The dispatch tool matches exactly: lowercase "task" is not a dispatch.
        int exit = command.Run(FactoryHookAdapter.Token, null, stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void PostDispatch_Block_WritesAdditionalContext()
    {
        const string note =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, note) };

        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PostToolUse", specific.GetProperty("hookEventName").GetString());
        Assert.Equal(note, specific.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Deny_AlsoWritesAdditionalContext()
    {
        const string note =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.Deny, note) };

        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            note,
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Annotation_WritesAdditionalContext()
    {
        const string note = "The dispatch returned without its report section.";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostAnnotation, note) };

        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            note,
            doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Allow_WritesNothing()
    {
        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void Pairing_NoCallId_PassesNullCallIdForDigestPairing()
    {
        ContextualEngine engine = new();

        // Factory documents no call id ([F11]), so the adapter fabricates none:
        // the engine receives a null call id for both phases and the ledger
        // pairs the return to its dispatch by pair-digest. The documented
        // session_id rides along for the ledger's scoping.
        (int preExit, _, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);
        (int postExit, _, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, preExit);
        Assert.Equal(0, postExit);
        Assert.Null(engine.SeenPreToolCallId);
        Assert.Null(engine.SeenPostToolCallId);
        Assert.Equal("session-factory-1", engine.SeenPreSessionId);
        Assert.Equal("session-factory-2", engine.SeenPostSessionId);
        Assert.Equal(1, engine.PostCalls);
    }

    [Fact]
    public void InternalError_WritesDenyShape()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", specific.GetProperty("permissionDecision").GetString());
        string reason = specific.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalError_OnReturn_AlsoWritesDenyShape()
    {
        ScriptedEngine engine = new() { ThrowOnPost = true };

        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", specific.GetProperty("permissionDecision").GetString());
        string reason = specific.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapters_AreRegisteredForFactoryToken()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = HarnessAdapterRegistry.CreateDefault(engine);

        Assert.True(registry.TryGet(FactoryHookAdapter.Token, out IHarnessHookAdapter? adapter));
        Assert.IsType<FactoryHookAdapter>(adapter);
    }
}
