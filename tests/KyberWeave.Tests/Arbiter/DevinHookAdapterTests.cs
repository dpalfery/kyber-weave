using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 16.7: the Devin harness adapter. Covers the dispatch
/// gate on <c>PreToolUse</c> with <c>tool_name == "run_subagent"</c>, the
/// target from <c>tool_input.profile</c> (the vendor documents only that the
/// tool "takes a profile") and the prompt from <c>tool_input.prompt</c>,
/// pairing by <c>pair-digest</c> ([F12]: the adapter passes a null call id and
/// the ledger pairs the return by digest), the answer shapes Devin documents
/// (a top-level <c>{"decision":"block","reason":…}</c> deny, a strip as
/// <c>hookSpecificOutput.updatedInput</c> merged into the arguments, a plain
/// pass writing nothing), <c>PostToolUse</c> reading
/// <c>tool_response.output</c> as the return and writing nothing (no feedback
/// field is documented), a dispatch with its profile or prompt absent treated
/// as unmarked — classified and logged, so <c>audit</c> reports it — and the
/// internal error writing the deny shape.
/// Fixture payloads live under <c>Fixtures/arbiter-hooks/devin/</c>, built
/// from [F12] of the task packet.
/// RED: <c>DevinHookAdapter</c> does not exist yet.
/// </summary>
public sealed class DevinHookAdapterTests
{
    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public string? SeenPreTarget;

        public string? SeenPrePrompt;

        public string? SeenPostToolOutput;

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
            SeenPostToolOutput = toolOutput;
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
            "Fixtures", "arbiter-hooks", "devin", name));

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
        int exit = command.Run(DevinHookAdapter.Token, caller, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    [Fact]
    public void PreDispatch_Deny_WritesTopLevelBlockShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = Run(Fixture("pre-run-subagent-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);

        // [F12]: a top-level decision block is what Devin documents to block with.
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("reason").GetString());
        Assert.False(doc.RootElement.TryGetProperty("hookSpecificOutput", out _));
    }

    [Fact]
    public void PreDispatch_Allow_SpecialistTarget_WritesMergeStrip()
    {
        (int exit, string stdout, _) = Run(Fixture("pre-run-subagent-dispatch.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");

        // The strip rides the documented merge field alone: permissionDecision
        // is not documented for Devin ([F12]), so no explicit allow is written.
        Assert.False(doc.RootElement.TryGetProperty("decision", out _));
        Assert.False(specific.TryGetProperty("permissionDecision", out _));

        // updatedInput is only the prompt: Devin merges it into the arguments,
        // so the original profile and any other fields must survive untouched.
        JsonElement updated = specific.GetProperty("updatedInput");
        Assert.Single(updated.EnumerateObject());
        Assert.Equal("Implement the adapter.", updated.GetProperty("prompt").GetString());
        Assert.DoesNotContain("KYBER-ARBITER", updated.GetProperty("prompt").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE", updated.GetProperty("prompt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_Allow_NonSpecialistTarget_WritesNothing()
    {
        string stdin = Fixture("pre-run-subagent-dispatch.json").Replace(
            "\"profile\": \"csharp-dev\"", "\"profile\": \"docs-dev\"", StringComparison.Ordinal);

        (int exit, string stdout, _) = Run(stdin, null, new ScriptedEngine());

        Assert.Equal(0, exit);

        // A plain pass writes nothing: the empty stdout on exit 0 is the
        // documented proceed.
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void PreDispatch_AllowWithRewrite_WritesRewrittenPromptOnly()
    {
        const string rewritten = """{"prompt":"Hello.","profile":"csharp-dev"}""";
        ScriptedEngine engine = new()
        {
            PreResult = new HookOutcome(HookOutcomeKind.AllowWithRewrite, RewrittenInputJson: rewritten),
        };

        (int exit, string stdout, _) = Run(Fixture("pre-run-subagent-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement updated = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.Single(updated.EnumerateObject());
        Assert.Equal("Hello.", updated.GetProperty("prompt").GetString());
    }

    [Fact]
    public void PreDispatch_TargetAndPrompt_ComeFromProfileAndPrompt()
    {
        ContextualEngine engine = new();

        (int exit, _, _) = Run(Fixture("pre-run-subagent-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(DevinHookAdapter.Token, engine.SeenPreHarness);
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
        Assert.StartsWith("KYBER-ARBITER: true", engine.SeenPrePrompt, StringComparison.Ordinal);
        Assert.Contains("Implement the adapter.", engine.SeenPrePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_MissingProfile_IsClassifiedAndLoggedAsUnmarked()
    {
        int loads = 0;
        ContextualEngine engine = new();

        // A dispatch with no profile is treated as unmarked, not malformed: it
        // is still classified — configuration loads and the ledger records the
        // event, so audit reports it — and the missing field is logged.
        (int exit, string stdout, string log) = Run(
            Fixture("pre-run-subagent-no-profile.json"), null, engine,
            (string repoRoot) => { loads++; return EnabledConfig(); });

        Assert.Equal(0, exit);
        Assert.Equal(1, loads);
        Assert.Equal(DevinHookAdapter.Token, engine.SeenPreHarness);
        Assert.Null(engine.SeenPreTarget);
        Assert.Equal("Implement the adapter.", engine.SeenPrePrompt);
        Assert.Contains("unmarked", log, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void PreDispatch_MissingPrompt_IsClassifiedAndLoggedAsUnmarked()
    {
        ContextualEngine engine = new();

        (int exit, string stdout, string log) = Run(
            Fixture("pre-run-subagent-no-prompt.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
        Assert.Equal(string.Empty, engine.SeenPrePrompt);
        Assert.Contains("unmarked", log, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout);
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

        // Plan reads stay advisory on Devin (design §12): the Read guard does
        // not apply, and a non-run_subagent tool passes through before
        // configuration loads.
        int exit = command.Run(DevinHookAdapter.Token, null, Fixture("pre-non-dispatch.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void PreDispatch_CaseDriftTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ContextualEngine engine = new();
        string stdin = Fixture("pre-run-subagent-dispatch.json").Replace(
            "\"tool_name\": \"run_subagent\"", "\"tool_name\": \"Run_Subagent\"", StringComparison.Ordinal);
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        // The dispatch tool matches exactly: "Run_Subagent" is not a dispatch.
        int exit = command.Run(DevinHookAdapter.Token, null, stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void PostDispatch_ReadsToolResponseOutput_AndWritesNothing()
    {
        const string note =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, note) };

        (int exit, string stdout, string log) = Run(Fixture("post-run-subagent-completed.json"), null, engine);

        Assert.Equal(0, exit);

        // The return content is the documented tool_response.output.
        Assert.Equal("Done.", engine.SeenPostToolOutput);

        // No feedback field is documented for PostToolUse ([F12]): Devin has no
        // channel to deliver the block through, so nothing is written. The
        // dropped decision is logged so it is not silent.
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("PostToolUse", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostDispatch_EveryOutcome_WritesNothing()
    {
        HookOutcome[] outcomes =
        [
            new(HookOutcomeKind.Allow),
            new(HookOutcomeKind.Deny, "denied"),
            new(HookOutcomeKind.PostBlock, "blocked"),
            new(HookOutcomeKind.PostAnnotation, "annotated"),
        ];

        foreach (HookOutcome outcome in outcomes)
        {
            ScriptedEngine engine = new() { PostResult = outcome };

            (int exit, string stdout, _) = Run(Fixture("post-run-subagent-completed.json"), null, engine);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stdout);
        }
    }

    [Fact]
    public void Pairing_NoCallId_PassesNullCallIdForDigestPairing()
    {
        ContextualEngine engine = new();

        // Devin documents no call id ([F12]), so the adapter fabricates none:
        // the engine receives a null call id for both phases and the ledger
        // pairs the return to its dispatch by pair-digest. The documented
        // session_id rides along for the ledger's scoping.
        (int preExit, _, _) = Run(Fixture("pre-run-subagent-dispatch.json"), null, engine);
        (int postExit, _, _) = Run(Fixture("post-run-subagent-completed.json"), null, engine);

        Assert.Equal(0, preExit);
        Assert.Equal(0, postExit);
        Assert.Null(engine.SeenPreToolCallId);
        Assert.Null(engine.SeenPostToolCallId);
        Assert.Equal("session-devin-1", engine.SeenPreSessionId);
        Assert.Equal("session-devin-5", engine.SeenPostSessionId);
        Assert.Equal(1, engine.PostCalls);
    }

    [Fact]
    public void InternalError_WritesDenyShape()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, _) = Run(Fixture("pre-run-subagent-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        string reason = doc.RootElement.GetProperty("reason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalError_OnReturn_AlsoWritesDenyShape()
    {
        ScriptedEngine engine = new() { ThrowOnPost = true };

        (int exit, string stdout, _) = Run(Fixture("post-run-subagent-completed.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        string reason = doc.RootElement.GetProperty("reason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapters_AreRegisteredForDevinToken()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = HarnessAdapterRegistry.CreateDefault(engine);

        Assert.True(registry.TryGet(DevinHookAdapter.Token, out IHarnessHookAdapter? adapter));
        Assert.IsType<DevinHookAdapter>(adapter);
    }
}
