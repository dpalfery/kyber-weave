using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 12.3: the Codex harness adapter. Covers the dispatch
/// matcher <c>^(Agent|(.*[._:/])?spawn_agent)$</c> with the target from
/// <c>tool_input.agent_type</c> and the prompt from <c>tool_input.message</c>,
/// the <c>PreToolUse</c> deny/strip/pass shapes, the <c>PostToolUse</c>
/// <c>additionalContext</c> shape (never <c>decision: block</c>), pairing by
/// <c>tool_use_id</c>, and the fail-closed deny shape.
/// Fixture payloads live under <c>Fixtures/arbiter-hooks/codex/</c>, built
/// from [F7] of the task packet.
/// RED: <c>CodexHookAdapter</c> does not exist yet.
/// </summary>
public sealed class CodexHookAdapterTests
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
            return PostResult;
        }
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "codex", name));

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
        int exit = command.Run(CodexHookAdapter.Token, caller, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    [Fact]
    public void PreDispatch_Deny_WritesDenyShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = Run(Fixture("pre-spawn-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, output.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void PreDispatch_Allow_SpecialistTarget_StripsCompleteInput()
    {
        (int exit, string stdout, _) = Run(Fixture("pre-spawn-dispatch.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());

        // updatedInput is the complete tool_input: every original field survives.
        JsonElement updated = output.GetProperty("updatedInput");
        Assert.Equal("csharp-dev", updated.GetProperty("agent_type").GetString());
        Assert.Equal("Implement T12.3", updated.GetProperty("task_name").GetString());
        string message = updated.GetProperty("message").GetString() ?? string.Empty;
        Assert.Equal("Implement the adapter.", message);
        Assert.DoesNotContain("KYBER-ARBITER", message, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_Allow_NonSpecialistTarget_WritesNothing()
    {
        string stdin = Fixture("pre-spawn-dispatch.json").Replace(
            "\"agent_type\": \"csharp-dev\"", "\"agent_type\": \"docs-dev\"", StringComparison.Ordinal);

        (int exit, string stdout, _) = Run(stdin, null, new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void PreDispatch_AgentAlias_TreatedAsDispatch()
    {
        (int exit, string stdout, _) = Run(Fixture("pre-agent-alias.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        string message = output.GetProperty("updatedInput").GetProperty("message").GetString() ?? string.Empty;
        Assert.Equal("Implement the adapter.", message);
    }

    [Fact]
    public void PreDispatch_PrefixedSpawnAgent_TreatedAsDispatch()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };
        string stdin = Fixture("pre-spawn-dispatch.json").Replace(
            "\"tool_name\": \"spawn_agent\"", "\"tool_name\": \"my-ns/spawn_agent\"", StringComparison.Ordinal);

        (int exit, string stdout, _) = Run(stdin, null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void PreDispatch_TargetAndPrompt_ComeFromAgentTypeAndMessage()
    {
        ScriptedEngine engine = new();

        (int exit, _, _) = Run(Fixture("pre-spawn-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
        Assert.StartsWith("KYBER-ARBITER: true", engine.SeenPrePrompt, StringComparison.Ordinal);
        Assert.Contains("Implement the adapter.", engine.SeenPrePrompt, StringComparison.Ordinal);
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

        int exit = command.Run(CodexHookAdapter.Token, null, Fixture("pre-non-dispatch.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
        Assert.Null(engine.SeenPreTarget);
    }

    [Fact]
    public void PreDispatch_NoTarget_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run(CodexHookAdapter.Token, null, Fixture("pre-spawn-no-target.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void PostDispatch_Block_WritesAdditionalContextNeverTopLevelBlock()
    {
        const string note =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, note) };

        (int exit, string stdout, _) = Run(Fixture("post-spawn-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.TryGetProperty("decision", out _));
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PostToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal(note, output.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Deny_AlsoWritesAdditionalContext()
    {
        const string note =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.Deny, note) };

        (int exit, string stdout, _) = Run(Fixture("post-spawn-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.TryGetProperty("decision", out _));
        Assert.Equal(note, doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Allow_WritesNothing()
    {
        (int exit, string stdout, _) = Run(Fixture("post-spawn-completed.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void Pairing_PreAndPost_UseToolUseId()
    {
        ContextualEngine engine = new();

        (int preExit, _, _) = Run(Fixture("pre-spawn-dispatch.json"), null, engine);
        (int postExit, _, _) = Run(Fixture("post-spawn-completed.json"), null, engine);

        Assert.Equal(0, preExit);
        Assert.Equal(0, postExit);
        Assert.Equal("toolu-codex-dispatch-1", engine.SeenPreToolCallId);
        Assert.Equal("toolu-codex-post-1", engine.SeenPostToolCallId);
        Assert.Equal("sess-codex-1", engine.SeenPreSessionId);
        Assert.Equal(1, engine.PostCalls);
    }

    [Fact]
    public void InternalError_WritesDenyShape()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, _) = Run(Fixture("pre-spawn-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        string reason = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapters_AreRegisteredForCodexToken()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = HarnessAdapterRegistry.CreateDefault(engine);

        Assert.True(registry.TryGet(CodexHookAdapter.Token, out IHarnessHookAdapter? adapter));
        Assert.IsType<CodexHookAdapter>(adapter);
    }
}
