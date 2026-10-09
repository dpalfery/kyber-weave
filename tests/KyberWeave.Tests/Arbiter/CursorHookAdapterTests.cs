using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 12.5: the Cursor harness adapter. Covers the dispatch
/// gate on <c>preToolUse</c> with <c>tool_name == "Task"</c>, the prompt from
/// <c>tool_input.prompt</c> and the undocumented target from
/// <c>tool_input.subagent_type</c> (absent means the target fact is absent and
/// the rules answer <c>undecidable</c>), pairing by <c>tool_use_id</c>, the
/// always-answer shapes (<c>permission</c> deny with <c>agent_message</c> and
/// <c>user_message</c>, <c>allow</c>, <c>allow</c> with the complete
/// <c>updated_input</c>, post-dispatch <c>additional_context</c> or
/// <c>{}</c>), and the fail-closed deny shape.
/// Fixture payloads live under <c>Fixtures/arbiter-hooks/cursor/</c>, built
/// from [F6] of the task packet.
/// RED: <c>CursorHookAdapter</c> does not exist yet.
/// </summary>
public sealed class CursorHookAdapterTests
{
    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public string? SeenPreTarget;

        public string? SeenPrePrompt;

        public bool ThrowOnPre;

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

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            PostResult;
    }

    private sealed class ContextualEngine : IContextualHookDecisionEngine
    {
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
            "Fixtures", "arbiter-hooks", "cursor", name));

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
        int exit = command.Run(CursorHookAdapter.Token, caller, stdin, stdout, log);
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
        Assert.Equal("deny", doc.RootElement.GetProperty("permission").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("agent_message").GetString());
        Assert.Equal("STATUS: ARBITER_ESCALATION", doc.RootElement.GetProperty("user_message").GetString());
    }

    [Fact]
    public void PreDispatch_Allow_SpecialistTarget_StripsCompleteInput()
    {
        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("allow", doc.RootElement.GetProperty("permission").GetString());

        // updated_input is the complete tool_input: every original field survives.
        JsonElement updated = doc.RootElement.GetProperty("updated_input");
        Assert.Equal("csharp-dev", updated.GetProperty("subagent_type").GetString());
        Assert.Equal("Implement T12.5", updated.GetProperty("description").GetString());
        string prompt = updated.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Implement the adapter.", prompt);
        Assert.DoesNotContain("KYBER-ARBITER", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_Allow_NonSpecialistTarget_WritesAllowOnly()
    {
        string stdin = Fixture("pre-task-dispatch.json").Replace(
            "\"subagent_type\": \"csharp-dev\"", "\"subagent_type\": \"docs-dev\"", StringComparison.Ordinal);

        (int exit, string stdout, _) = Run(stdin, null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("allow", doc.RootElement.GetProperty("permission").GetString());
        Assert.False(doc.RootElement.TryGetProperty("updated_input", out _));
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
        Assert.Equal("allow", doc.RootElement.GetProperty("permission").GetString());
        Assert.Equal("Hello.", doc.RootElement.GetProperty("updated_input").GetProperty("prompt").GetString());
    }

    [Fact]
    public void PreDispatch_TargetAndPrompt_ComeFromSubagentTypeAndPrompt()
    {
        ScriptedEngine engine = new();

        (int exit, _, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
        Assert.StartsWith("KYBER-ARBITER: true", engine.SeenPrePrompt, StringComparison.Ordinal);
        Assert.Contains("Implement the adapter.", engine.SeenPrePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_MissingTarget_CallsEngineWithAbsentTarget()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: undecidable (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };
        int loads = 0;
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // The target fact is absent, so the rules answer undecidable (escalated
        // here): the event is classified, not pass-through.
        int exit = command.Run(CursorHookAdapter.Token, null, Fixture("pre-task-no-target.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(1, loads);
        Assert.Null(engine.SeenPreTarget);
        Assert.StartsWith("KYBER-ARBITER: true", engine.SeenPrePrompt, StringComparison.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("deny", doc.RootElement.GetProperty("permission").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("agent_message").GetString());
        Assert.Equal("STATUS: ARBITER_ESCALATION", doc.RootElement.GetProperty("user_message").GetString());
    }

    [Fact]
    public void PreDispatch_NonDispatchTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run(CursorHookAdapter.Token, null, Fixture("pre-non-dispatch.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
        Assert.Null(engine.SeenPreTarget);
    }

    [Fact]
    public void PreDispatch_LowercaseTaskTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        string stdin = Fixture("pre-task-dispatch.json").Replace(
            "\"tool_name\": \"Task\"", "\"tool_name\": \"task\"", StringComparison.Ordinal);
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // The dispatch tool matches exactly: lowercase "task" is not a dispatch.
        int exit = command.Run(CursorHookAdapter.Token, null, stdin, stdout, log);

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
        Assert.Equal(note, doc.RootElement.GetProperty("additional_context").GetString());
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
        Assert.Equal(note, doc.RootElement.GetProperty("additional_context").GetString());
    }

    [Fact]
    public void PostDispatch_Allow_WritesEmptyObject()
    {
        (int exit, string stdout, _) = Run(Fixture("post-task-completed.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout);
    }

    [Fact]
    public void Pairing_PreAndPost_UseToolUseId()
    {
        ContextualEngine engine = new();

        (int preExit, _, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);
        (int postExit, _, _) = Run(Fixture("post-task-completed.json"), null, engine);

        Assert.Equal(0, preExit);
        Assert.Equal(0, postExit);
        Assert.Equal("toolu-cursor-dispatch-1", engine.SeenPreToolCallId);
        Assert.Equal("toolu-cursor-post-1", engine.SeenPostToolCallId);
        Assert.Equal("conv-cursor-1", engine.SeenPreSessionId);
        Assert.Equal("conv-cursor-4", engine.SeenPostSessionId);
        Assert.Equal(1, engine.PostCalls);
    }

    [Fact]
    public void InternalError_WritesDenyShape()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, _) = Run(Fixture("pre-task-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("permission").GetString());
        string agentMessage = doc.RootElement.GetProperty("agent_message").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, agentMessage, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", agentMessage, StringComparison.Ordinal);
        string userMessage = doc.RootElement.GetProperty("user_message").GetString() ?? string.Empty;
        Assert.NotEmpty(userMessage);
        Assert.DoesNotContain("\n", userMessage, StringComparison.Ordinal);
        Assert.StartsWith("STATUS: ARBITER_ESCALATION", userMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapters_AreRegisteredForCursorToken()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = HarnessAdapterRegistry.CreateDefault(engine);

        Assert.True(registry.TryGet(CursorHookAdapter.Token, out IHarnessHookAdapter? adapter));
        Assert.IsType<CursorHookAdapter>(adapter);
    }
}
