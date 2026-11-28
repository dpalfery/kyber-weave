using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using Xunit;
using KyberWeave.Tests.Corpus;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.1: the Claude harness adapter. Covers the deny, allow, strip,
/// post block, annotation and launch shapes from the fixture payloads under
/// <c>Fixtures/arbiter-hooks/claude/</c> (built from [F1] of the task packet), the
/// <c>Task</c> alias, and that the payload's <c>agent_type</c> outranks
/// <c>--caller</c>.
/// RED: the Arbiter project and the Claude adapter do not exist yet.
/// </summary>
public sealed class ClaudeHookAdapterTests
{
    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public string? SeenPreCaller;

        public string? SeenPostCaller;

        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config)
        {
            SeenPreCaller = caller;
            return PreResult;
        }

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            SeenPostCaller = caller;
            return PostResult;
        }
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(caller, target, prompt, config));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string caller,
            string? target,
            string toolOutput,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(caller, target, toolOutput, config));
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static string Fixture(string name) =>
        EmbeddedFixtureText.Read(typeof(ClaudeHookAdapterTests).Assembly, $"arbiter-hooks/claude/{name}");

    private static (int Exit, string Stdout) Run(
        string stdin, string? caller, IHookDecisionEngine engine)
    {
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            _ => EnabledConfig(),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run("claude", caller, stdin, stdout, log);
        return (exit, stdout.ToString());
    }

    [Fact]
    public void PreDispatch_Deny_WritesDenyShapeWithEnvelope()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout) = Run(Fixture("pre-agent-dispatch.json"), "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, output.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void PreDispatch_Allow_NonSpecialistTarget_WritesNothing()
    {
        string stdin = Fixture("pre-agent-dispatch.json").Replace(
            "\"subagent_type\": \"dev-medium\"", "\"subagent_type\": \"docs-dev\"", StringComparison.Ordinal);

        (int exit, string stdout) = Run(stdin, "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void PreDispatch_Allow_SpecialistTarget_StripsHeaderBlock()
    {
        (int exit, string stdout) = Run(Fixture("pre-agent-dispatch.json"), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());

        // updatedInput is the complete tool_input: every original field survives.
        JsonElement updated = output.GetProperty("updatedInput");
        Assert.Equal("Implement T3", updated.GetProperty("description").GetString());
        Assert.Equal("dev-medium", updated.GetProperty("subagent_type").GetString());
        string prompt = updated.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Implement the plan parser.", prompt);
        Assert.DoesNotContain("KYBER-ARBITER", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PreDispatch_TaskAlias_TreatedAsDispatch()
    {
        (int exit, string stdout) = Run(Fixture("pre-task-dispatch.json"), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        string prompt = output.GetProperty("updatedInput").GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Write the tests.", prompt);
    }

    [Fact]
    public void PostDispatch_Completed_NonAllow_WritesTopLevelBlock()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, envelope) };

        (int exit, string stdout) = Run(Fixture("post-completed.json"), "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void PostDispatch_Annotation_WritesAdditionalContext()
    {
        const string note = "STATUS: ARBITER_ANNOTATION\nFINDING: correctness/null-plan-task";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostAnnotation, note) };

        (int exit, string stdout) = Run(Fixture("post-completed.json"), "code-reviewer", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PostToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal(note, output.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void PostDispatch_Completed_Allow_WritesNothing()
    {
        (int exit, string stdout) = Run(Fixture("post-completed.json"), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void PostDispatch_AsyncLaunched_HandsTheDispatchToTheEngineAndWritesNothing()
    {
        BackgroundScriptedEngine engine = new();

        (int exit, string stdout) = Run(Fixture("post-async-launched.json"), "conductor", engine);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        BackgroundDispatch tracked = Assert.Single(engine.Tracked);
        Assert.Equal(
            new BackgroundDispatch(
                "claude",
                "sess-post-2",
                "agent-9",
                "conductor",
                "dev-medium",
                "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T4\n\nImplement the next task.",
                "toolu-launch-1"),
            tracked);
    }

    [Theory]
    [InlineData(
        """{"hook_event_name":"PostToolUse","tool_name":"Agent","session_id":"s","tool_use_id":"t"}""",
        "carries no tool_response")]
    [InlineData(
        """{"hook_event_name":"PostToolUse","tool_name":"Agent","session_id":"s","tool_response":{"status":"weird"}}""",
        "unknown PostToolUse status")]
    [InlineData(
        """{"hook_event_name":"PreToolUse","tool_name":"SubagentHandback","session_id":"s","tool_input":{"message":"hi"}}""",
        "carries no agent_id")]
    [InlineData(
        """{"hook_event_name":"PreToolUse","tool_name":"SubagentHandback","session_id":"s","agent_id":"a","tool_input":{}}""",
        "carries no message")]
    public void MalformedDispatchInput_FailsOpenAndLogsTheGuard(string stdin, string expected)
    {
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(new ScriptedEngine()),
            _ => EnabledConfig(),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run("claude", "conductor", stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.Contains(expected, log.ToString(), StringComparison.Ordinal);
        Assert.Contains("The dispatch continues without an Arbiter decision.", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A plain engine that also follows background dispatches, recording what it is handed.</summary>
    private sealed class BackgroundScriptedEngine : IHookDecisionEngine, IBackgroundDispatchEngine
    {
        public List<BackgroundDispatch> Tracked { get; } = [];

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public void TrackBackgroundDispatch(BackgroundDispatch dispatch, HookContext context) => Tracked.Add(dispatch);

        public BackgroundDispatchRecord? FindBackgroundDispatch(string sessionId, string agentId, HookContext context) => null;

        public HookOutcome DecideBackgroundReturn(
            string harness, string sessionId, string agentId, string response, KyberWeaveConfig config, HookContext context) =>
            new(HookOutcomeKind.Allow);

        public IReadOnlyList<string> TakePendingNotes(string sessionId, string caller, HookContext context) => [];
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(caller, target, prompt, config));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string caller,
            string? target,
            string toolOutput,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(caller, target, toolOutput, config));
        /// <inheritdoc/>
        public Task<HookOutcome> DecideBackgroundReturnAsync(
            string harness,
            string sessionId,
            string agentId,
            string response,
            KyberWeaveConfig config,
            HookContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecideBackgroundReturn(harness, sessionId, agentId, response, config, context));
    }

    [Fact]
    public void Caller_PayloadAgentType_OutranksCallerFlag()
    {
        ScriptedEngine engine = new();

        Run(Fixture("pre-agent-dispatch.json"), "dev-medium", engine);

        Assert.Equal("conductor", engine.SeenPreCaller);
    }

    [Fact]
    public void Caller_FallsBackToCallerFlagWithoutPayloadAgentType()
    {
        string stdin = Fixture("pre-agent-dispatch.json").Replace(
            "\"agent_type\": \"conductor\",", string.Empty, StringComparison.Ordinal);
        ScriptedEngine engine = new();

        (int exit, _) = Run(stdin, "dev-medium", engine);

        Assert.Equal(0, exit);
        Assert.Equal("dev-medium", engine.SeenPreCaller);
    }

    // Model rewrite (task 2.7). Claude's PreToolUse hook takes an updatedInput that replaces the whole
    // tool input (https://code.claude.com/docs/en/hooks), and the Agent tool's model parameter takes an
    // alias: sonnet, opus, haiku or fable (https://code.claude.com/docs/en/sub-agents).

    /// <summary>A contextual engine: it records the dispatch model the adapter hands it, and returns a fixed pre outcome.</summary>
    private sealed class ContextualScriptedEngine : IContextualHookDecisionEngine
    {
        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public string? SeenDispatchModel { get; private set; }

        /// <summary>When set, the engine asks the adapter to write this token, and allows with no token if the write does not hold.</summary>
        public string? ConfirmToken { get; init; }

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            PreResult;

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null) =>
            DecidePreDispatch(harness, caller, target, prompt, config, context, toolCallId, sessionId, dispatchModel: null);

        /// <summary>The dispatch-model overload the adapter must hand the model to (task 2.7 RED: the interface lacks it).</summary>
        public HookOutcome DecidePreDispatch(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId,
            string? sessionId,
            string? dispatchModel)
        {
            SeenDispatchModel = dispatchModel;
            if (ConfirmToken is null)
            {
                return PreResult;
            }

            bool held = context.ConfirmModelWrite?.Invoke(ConfirmToken) ?? false;
            return held
                ? new HookOutcome(HookOutcomeKind.Allow, ModelToken: ConfirmToken)
                : new HookOutcome(HookOutcomeKind.Allow);
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
            string? sessionId = null) =>
            new(HookOutcomeKind.Allow);
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(caller, target, prompt, config));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string caller,
            string? target,
            string toolOutput,
            KyberWeaveConfig config,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(caller, target, toolOutput, config));
        /// <inheritdoc/>
        public Task<HookOutcome> DecidePreDispatchAsync(
            string harness,
            string caller,
            string? target,
            string prompt,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId,
            string? sessionId,
            string? dispatchModel,
            string? deliveredPrompt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePreDispatch(
                harness, caller, target, prompt, config, context, toolCallId, sessionId));

        /// <inheritdoc/>
        public Task<HookOutcome> DecidePostDispatchAsync(
            string harness,
            string caller,
            string? target,
            string prompt,
            string toolOutput,
            KyberWeaveConfig config,
            HookContext context,
            string? toolCallId = null,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(DecidePostDispatch(
                harness, caller, target, prompt, toolOutput, config, context, toolCallId, sessionId));
    }

    /// <summary>An Agent PreToolUse payload from the shipped fixture, with its tool input replaced by the given fields.</summary>
    /// <remarks>
    /// The tool input also carries fields the adapter never reads (<c>run_in_background</c>, <c>isolation</c>, a nested
    /// object and an array). Every rewrite must hand them back unchanged: <see cref="AssertExtraFieldsSurvive"/>.
    /// </remarks>
    private static string DispatchPayload(string prompt, string? model = null, string subagent = "dev-medium")
    {
        JsonObject input = new()
        {
            ["subagent_type"] = subagent,
            ["prompt"] = prompt,
            ["description"] = "Implement T2",
            ["run_in_background"] = true,
            ["isolation"] = "worktree",
            ["options"] = new JsonObject { ["nested"] = new JsonObject { ["depth"] = 2 } },
            ["files"] = new JsonArray(JsonValue.Create("src/A.cs"), JsonValue.Create("src/B.cs")),
        };
        if (model is not null)
        {
            input["model"] = model;
        }

        JsonNode root = JsonNode.Parse(Fixture("pre-agent-dispatch.json"))
            ?? throw new InvalidOperationException("The shipped Claude fixture is not a JSON document.");
        root["tool_input"] = input;
        return root.ToJsonString();
    }

    private const string HeaderedPrompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: T2\n\nImplement T2.";

    private static (int Exit, string Stdout, string Log) RunContextual(string stdin, IHookDecisionEngine engine)
    {
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            _ => EnabledConfig(),
            () => "decision-test-27");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run("claude", "conductor", stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    private static JsonElement UpdatedInput(string stdout)
    {
        using JsonDocument doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput").Clone();
    }

    [Fact]
    public void ModelRewrite_UpdatedInputIsOneDocument_KeepingEveryFieldAndStrippingTheHeader()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: "sonnet") };

        (int exit, string stdout, _) = RunContextual(DispatchPayload(HeaderedPrompt), engine);

        Assert.Equal(0, exit);
        JsonElement updated = UpdatedInput(stdout);
        Assert.Equal("dev-medium", updated.GetProperty("subagent_type").GetString());
        Assert.Equal("Implement T2", updated.GetProperty("description").GetString());
        Assert.Equal("Implement T2.", updated.GetProperty("prompt").GetString());
        Assert.Equal("sonnet", updated.GetProperty("model").GetString());
    }

    [Fact]
    public void ModelRewrite_WritesTheModelEvenWhenNoHeaderWasStripped()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: "opus") };

        (int exit, string stdout, _) = RunContextual(DispatchPayload("Implement T2."), engine);

        Assert.Equal(0, exit);
        JsonElement updated = UpdatedInput(stdout);
        Assert.Equal("Implement T2.", updated.GetProperty("prompt").GetString());
        Assert.Equal("opus", updated.GetProperty("model").GetString());
    }

    [Fact]
    public void ModelRewrite_AnAliasToken_IsWrittenAsItIs()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: "haiku") };

        (_, string stdout, _) = RunContextual(DispatchPayload(HeaderedPrompt), engine);

        Assert.Equal("haiku", UpdatedInput(stdout).GetProperty("model").GetString());
    }

    [Fact]
    public void ModelRewrite_AFullModelIdentifier_WritesItsFamilyAliasAndRecordsAliasDerived()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: "claude-sonnet-5-5") };

        (_, string stdout, string log) = RunContextual(DispatchPayload(HeaderedPrompt), engine);

        Assert.Equal("sonnet", UpdatedInput(stdout).GetProperty("model").GetString());
        Assert.Contains("model.reason: alias-derived", log, StringComparison.Ordinal);
        Assert.Contains("claude-sonnet-5-5", log, StringComparison.Ordinal);
        Assert.Contains("sonnet", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelRewrite_AnOtherToken_IsNotWritten_AndRecordsTokenNotAccepted()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: "gpt-4o") };

        (_, string stdout, string log) = RunContextual(DispatchPayload(HeaderedPrompt), engine);

        Assert.False(UpdatedInput(stdout).TryGetProperty("model", out _));
        Assert.Contains("token-not-accepted", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("claude-unknown-9")]
    [InlineData("claude")]
    [InlineData("")]
    public void ModelRewrite_TokenWithNoAcceptedFamily_IsNotWritten(string token)
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: token) };

        (_, string stdout, string log) = RunContextual(DispatchPayload(HeaderedPrompt), engine);

        Assert.False(UpdatedInput(stdout).TryGetProperty("model", out _));
        Assert.Contains("token-not-accepted", log, StringComparison.Ordinal);
    }

    private static readonly string[] ExpectedFiles = ["src/A.cs", "src/B.cs"];

    /// <summary>The tool-input fields <see cref="DispatchPayload"/> adds beyond the three the adapter reads.</summary>
    private static void AssertExtraFieldsSurvive(JsonElement updated)
    {
        Assert.True(updated.GetProperty("run_in_background").GetBoolean());
        Assert.Equal("worktree", updated.GetProperty("isolation").GetString());
        Assert.Equal(2, updated.GetProperty("options").GetProperty("nested").GetProperty("depth").GetInt32());
        Assert.Equal(JsonValueKind.Array, updated.GetProperty("files").ValueKind);
        Assert.Equal(ExpectedFiles, updated.GetProperty("files").EnumerateArray().Select(file => file.GetString()).ToArray());
    }

    [Fact]
    public void ModelRewrite_WrittenModel_KeepsEveryOtherToolInputField()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: "sonnet") };

        (_, string stdout, _) = RunContextual(DispatchPayload(HeaderedPrompt), engine);

        AssertExtraFieldsSurvive(UpdatedInput(stdout));
    }

    [Fact]
    public void ModelRewrite_FailedWrite_KeepsEveryOtherToolInputField()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: "sonnet") };
        ClaudeHookAdapter adapter = new(engine) { ModelWriteFault = new IOException("write refused") };
        using JsonDocument doc = JsonDocument.Parse(DispatchPayload(HeaderedPrompt));
        using StringWriter log = new();
        HookContext context = new(ArbiterTestRoots.Under("claude-hook"), () => "decision-27", log);

        string stdout = adapter.Handle(doc.RootElement, DispatchPayload(HeaderedPrompt), "conductor", EnabledConfig(), context);

        AssertExtraFieldsSurvive(UpdatedInput(stdout));
    }

    [Fact]
    public void ModelRewrite_StripOnly_KeepsEveryOtherToolInputField()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow) };

        (_, string stdout, _) = RunContextual(DispatchPayload(HeaderedPrompt), engine);

        AssertExtraFieldsSurvive(UpdatedInput(stdout));
    }

    [Fact]
    public void ModelRewrite_ACallerSuppliedModelField_IsPassedAsDispatchModelAndLeftUnchanged()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow) };

        (_, string stdout, _) = RunContextual(DispatchPayload(HeaderedPrompt, model: "claude-opus-5-5"), engine);

        Assert.Equal("claude-opus-5-5", engine.SeenDispatchModel);
        Assert.Equal("claude-opus-5-5", UpdatedInput(stdout).GetProperty("model").GetString());
    }

    [Fact]
    public void ModelWriteFailure_FallsBackToStripOnly_LogsModelWriteFailedAndDeliveredFalse()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow, ModelToken: "sonnet") };
        ClaudeHookAdapter adapter = new(engine) { ModelWriteFault = new IOException("write refused") };
        using JsonDocument doc = JsonDocument.Parse(DispatchPayload(HeaderedPrompt));
        using StringWriter log = new();
        HookContext context = new(ArbiterTestRoots.Under("claude-hook"), () => "decision-27", log);

        string stdout = adapter.Handle(doc.RootElement, DispatchPayload(HeaderedPrompt), "conductor", EnabledConfig(), context);

        JsonElement updated = UpdatedInput(stdout);
        Assert.Equal("Implement T2.", updated.GetProperty("prompt").GetString());
        Assert.False(updated.TryGetProperty("model", out _));
        Assert.Contains("model-write-failed", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("delivered: false", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: a failed model write must still allow the dispatch with the routing header stripped, for every
    /// target class. The GREEN change once stopped stripping for non-implementation targets when the write failed,
    /// which left the header in the prompt the subagent received.
    /// </summary>
    [Theory]
    [InlineData("dev-medium")]
    [InlineData("code-reviewer")]
    public void ModelWriteFailure_StripsTheHeaderForEveryTargetClass(string subagent)
    {
        ContextualScriptedEngine engine = new() { ConfirmToken = "sonnet" };
        ClaudeHookAdapter adapter = new(engine) { ModelWriteFault = new IOException("write refused") };
        string raw = DispatchPayload(HeaderedPrompt, subagent: subagent);
        using JsonDocument doc = JsonDocument.Parse(raw);
        using StringWriter log = new();
        HookContext context = new(ArbiterTestRoots.Under("claude-hook"), () => "decision-27", log);

        string stdout = adapter.Handle(doc.RootElement, raw, "conductor", EnabledConfig(), context);

        Assert.False(string.IsNullOrEmpty(stdout), $"{subagent}: the dispatch must be allowed, not passed through");
        JsonElement updated = UpdatedInput(stdout);
        Assert.Equal("Implement T2.", updated.GetProperty("prompt").GetString());
        Assert.False(updated.TryGetProperty("model", out _));
        Assert.True(updated.GetProperty("run_in_background").GetBoolean());
        Assert.Equal(subagent, updated.GetProperty("subagent_type").GetString());
        Assert.Contains("model-write-failed", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A scripted allow with no token renders the strip-only document. The scripted engine never runs shadow
    /// mode: the real shadow path is <c>ArbiterModelDeliveryTests.ModeShadow_HookOutcomeCarriesNoToken_ButTheDecisionStillHoldsTheModelBlock</c>.
    /// </summary>
    [Fact]
    public void ModelRewrite_ScriptedAllowWithNoToken_IsTheStripOnlyDocument()
    {
        ContextualScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Allow) };

        (int exit, string stdout, _) = RunContextual(DispatchPayload(HeaderedPrompt), engine);

        Assert.Equal(0, exit);
        JsonElement updated = UpdatedInput(stdout);
        Assert.Equal("Implement T2.", updated.GetProperty("prompt").GetString());
        Assert.False(updated.TryGetProperty("model", out _));
    }

    // Shell guard (KW-ARB-SHELL-001). The architect's Bash and PowerShell calls are checked against its
    // documented command list before they run; other callers keep the behaviour they had.

    /// <summary>A Bash or PowerShell PreToolUse payload from the shipped fixture, with the given command and agent type.</summary>
    private static string ShellPayload(string tool, string command, string? agentType)
    {
        JsonNode root = JsonNode.Parse(Fixture("pre-bash-denied.json"))
            ?? throw new InvalidOperationException("The shipped Claude Bash fixture is not a JSON document.");
        root["tool_name"] = tool;
        root["tool_input"] = new JsonObject { ["command"] = command };
        if (agentType is null)
        {
            root.AsObject().Remove("agent_type");
        }
        else
        {
            root["agent_type"] = agentType;
        }

        return root.ToJsonString();
    }

    private static void AssertShellDenied(string stdout)
    {
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Contains(
            "KW-ARB-SHELL-001",
            output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bash", "grep x | sed y")]
    [InlineData("Bash", "python3 x.py")]
    [InlineData("PowerShell", "grep x | sed y")]
    [InlineData("PowerShell", "python3 x.py")]
    public void ShellGuard_ArchitectCallOutsideTheList_IsDeniedBeforeItRuns_WhenTheAgentTypeNamesIt(string tool, string command)
    {
        // The conductor is the --caller flag; the payload's agent_type must outrank it.
        (int exit, string stdout) = Run(ShellPayload(tool, command, "architect"), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        AssertShellDenied(stdout);
    }

    [Fact]
    public void ShellGuard_ArchitectCallOutsideTheList_IsDeniedWhenTheRenderedCallerNamesIt()
    {
        (int exit, string stdout) = Run(ShellPayload("Bash", "python3 x.py", agentType: null), "architect", new ScriptedEngine());

        Assert.Equal(0, exit);
        AssertShellDenied(stdout);
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    public void ShellGuard_ArchitectCallOnTheList_PassesThroughSilently(string tool)
    {
        (int exit, string stdout) = Run(ShellPayload(tool, "git status", "architect"), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void ReadGuard_CsharpDevPowerShellCall_DeniesAPlanningPathRead()
    {
        string planningDirectory = ReadGuard.ProtectedDirectories(new KyberWeaveConfig { Arbiter = ArbiterConfigDefaults.Product })[0];

        (int exit, string stdout) = Run(
            ShellPayload("PowerShell", $"Get-Content {planningDirectory}/plan.md", "dev-medium"),
            "conductor",
            new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Contains("Req 25", output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadGuard_CsharpDevPowerShellCall_AllowsAnOrdinaryCommand()
    {
        (int exit, string stdout) = Run(
            ShellPayload("PowerShell", "Get-ChildItem src", "dev-medium"),
            "conductor",
            new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void ShellGuard_CsharpDevBashCall_KeepsTheWorkerPlanningPathDeny()
    {
        // grep is on the architect's list, so an allow here would mean the shell guard reached dev-medium.
        string planningDirectory = ReadGuard.ProtectedDirectories(new KyberWeaveConfig { Arbiter = ArbiterConfigDefaults.Product })[0];

        (int exit, string stdout) = Run(
            ShellPayload("Bash", $"grep -rn x {planningDirectory}", "dev-medium"),
            "conductor",
            new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        string reason = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains("Req 25", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-ARB-SHELL-001", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellGuard_ConductorBashCall_IsPassThrough()
    {
        (int exit, string stdout) = Run(ShellPayload("Bash", "python3 x.py", "conductor"), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void ShellGuard_BashCallWithNoCaller_IsPassThrough()
    {
        (int exit, string stdout) = Run(ShellPayload("Bash", "python3 x.py", agentType: null), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void ShellGuard_ArchitectBashCallWithoutToolInput_IsDeniedAndLogged()
    {
        // A call the guard cannot read is not a call it may let through: it is denied, and the log names the rule.
        JsonNode root = JsonNode.Parse(ShellPayload("Bash", "git status", "architect"))
            ?? throw new InvalidOperationException("The shell payload is not a JSON document.");
        root.AsObject().Remove("tool_input");
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(new ScriptedEngine()),
            _ => EnabledConfig(),
            () => "decision-shell-no-input");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run("claude", "conductor", root.ToJsonString(), stdout, log);

        Assert.Equal(0, exit);
        AssertShellDenied(stdout.ToString());
        Assert.Contains("KW-ARB-SHELL-001", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The notice Claude Code returns from a foreground lens dispatch whose report went through its hand-back.</summary>
    private const string Wrapper =
        "This agent's report was delivered to you as a message from \"a52bf17cfe6305406\" (its SubagentHandback call). " +
        "Read it there; it is not repeated here.";

    private const string LensReport = "NO FINDINGS: nothing to report\nREVIEW.md sections checked: all";

    [Fact]
    public void DeliveryWrapper_IsRecognisedOnlyAsAWholeReply()
    {
        Assert.True(ClaudeDeliveryWrapper.IsWrapper(Wrapper));
        Assert.True(ClaudeDeliveryWrapper.IsWrapper("\n  " + Wrapper + "\n"));
        Assert.False(ClaudeDeliveryWrapper.IsWrapper(Wrapper + "\n\nF1: the seat holds the wrapper."));
        Assert.False(ClaudeDeliveryWrapper.IsWrapper("Quoting the harness: " + Wrapper));
        Assert.False(ClaudeDeliveryWrapper.IsWrapper(null));
        Assert.False(ClaudeDeliveryWrapper.IsWrapper("  "));
    }

    /// <summary>Today's order: the hand-back carries the report first, then the completed post carries only the notice.</summary>
    [Fact]
    public void HandbackThenWrapperPost_TheSeatHoldsTheReportOnce_AndTheReturnPairsWithItsPre()
    {
        using DeliveryRig rig = new();
        rig.Dispatch();

        rig.Handback(LensReport);
        rig.CompletedPost(Wrapper);

        Assert.Equal(LensReport, rig.SeatText());
        Assert.Single(rig.Posts(), p => p.PreId == rig.Pre().Id);
    }

    /// <summary>The completed post arrives before the hand-back: the report still reaches the seat once and pairs.</summary>
    [Fact]
    public void WrapperPostThenHandback_TheSeatHoldsTheReportOnce_AndTheReturnPairsWithItsPre()
    {
        using DeliveryRig rig = new();
        rig.Dispatch();

        rig.CompletedPost(Wrapper);
        Assert.False(rig.SeatExists());
        rig.Handback(LensReport);

        Assert.Equal(LensReport, rig.SeatText());
        Assert.Single(rig.Posts(), p => p.PreId == rig.Pre().Id);
    }

    /// <summary>With no hand-back, the stop event's last assistant message is the report, in either order.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrapperPostWithNoHandback_TheStopEventsLastAssistantMessageIsSaved(bool stopFirst)
    {
        using DeliveryRig rig = new();
        rig.Dispatch();

        if (stopFirst)
        {
            rig.Stop(LensReport);
            rig.CompletedPost(Wrapper);
        }
        else
        {
            rig.CompletedPost(Wrapper);
            rig.Stop(LensReport);
        }

        // A stop that comes first also closes the dispatch with a recovery row, as it does today; the report that
        // follows still pairs with it, once.
        Assert.Equal(LensReport, rig.SeatText());
        Assert.Single(rig.Posts(), p => p.PreId == rig.Pre().Id && p.Recovered is null);
    }

    /// <summary>A notice with no report anywhere records nothing and says so in one line.</summary>
    [Fact]
    public void WrapperPostWithNoReport_SavesNoSeat_AndLogsOneLine()
    {
        using DeliveryRig rig = new();
        rig.Dispatch();

        string log = rig.CompletedPost(Wrapper);

        Assert.False(rig.SeatExists());
        Assert.DoesNotContain(rig.Posts(), p => p.PreId == rig.Pre().Id);
        Assert.Single(
            log.Split('\n'),
            line => line.Contains("carries only the delivery notice", StringComparison.Ordinal));
    }

    /// <summary>A completed post that carries the report itself is saved as today.</summary>
    [Fact]
    public void CompletedPostWithARealReply_SavesItAsToday()
    {
        using DeliveryRig rig = new();
        rig.Dispatch();

        rig.CompletedPost(LensReport);

        Assert.Equal(LensReport, rig.SeatText());
        Assert.Single(rig.Posts(), p => p.PreId == rig.Pre().Id);
    }

    /// <summary>A followed background dispatch still records its hand-back as its return, and its notice adds nothing.</summary>
    [Fact]
    public void FollowedBackgroundDispatch_StillRecordsItsHandbackAsToday()
    {
        using DeliveryRig rig = new();
        rig.Dispatch(background: true);
        rig.Launched();

        rig.Handback(LensReport);
        rig.CompletedPost(Wrapper);

        Assert.Equal(LensReport, rig.SeatText());
        Assert.Single(rig.Posts(), p => p.PreId == rig.Pre().Id);
    }

    /// <summary>
    /// One lens dispatch's hook events, each sent as its own process would send it: a fresh command over a fresh
    /// engine, sharing only the in-memory host's store, ledger and files.
    /// </summary>
    private sealed class DeliveryRig : IDisposable
    {
        private const string Session = "session-lens-1";
        private const string ToolUseId = "toolu-lens-1";
        private const string AgentId = "a52bf17cfe6305406";

        private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

        private readonly FakeArbiterProvider _provider = new()
        {
            DefaultAnswer = ArbiterStep1BatchResult.NotEvaluated,
            ScoreDefaultAnswer = true,
            Confidence = 0.95,
            Model = "stub",
        };

        private static readonly KyberWeaveConfig Config = new()
        {
            Arbiter = new ArbiterConfig { Enabled = true, Rules = [] },
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
        };

        public void Dispose() => _host.Dispose();

        private string Cycle => Path.Combine(_host.RepoRoot, ".agent_scratchpad", "feat", "review", "cycle-1");

        private string Seat => Path.Combine(Cycle, "seats", "correctness.md");

        private string Prompt =>
            "KYBER-ARBITER: true\nLENS: correctness\nREVIEW_RUN: run-1\n\nPacket: "
            + Path.Combine(Cycle, "packets", "correctness.md") + "\n\nApply the lens.";

        public bool SeatExists() => _host.Files.FileExists(Seat);

        public string SeatText() => _host.Files.ReadAllText(Seat);

        public ArbiterLedgerEvent Pre() =>
            Assert.Single(_host.Ledger.ReadAll(), e => e.Phase == ArbiterLedgerPhases.Pre && e.CallId == ToolUseId);

        public IReadOnlyList<ArbiterLedgerEvent> Posts() =>
            [.. _host.Ledger.ReadAll().Where(e => e.Phase == ArbiterLedgerPhases.Post)];

        public void Dispatch(bool background = false) => Send(new
        {
            hook_event_name = "PreToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            agent_type = "code-reviewer",
            tool_name = "Agent",
            tool_input = new { description = "correctness", prompt = Prompt, subagent_type = "review-lens", run_in_background = background },
            tool_use_id = ToolUseId,
        });

        public void Launched() => Send(new
        {
            hook_event_name = "PostToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            agent_type = "code-reviewer",
            tool_name = "Agent",
            tool_input = new { description = "correctness", prompt = Prompt, subagent_type = "review-lens", run_in_background = true },
            tool_use_id = ToolUseId,
            tool_response = new { status = "async_launched", agentId = AgentId, content = Array.Empty<string>() },
        });

        public void Handback(string message) => Send(new
        {
            hook_event_name = "PreToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            agent_id = AgentId,
            agent_type = "review-lens",
            tool_name = "SubagentHandback",
            tool_input = new { message },
            tool_use_id = "toolu-handback-1",
        });

        public string CompletedPost(string text) => Send(new
        {
            hook_event_name = "PostToolUse",
            session_id = Session,
            cwd = _host.RepoRoot,
            agent_type = "code-reviewer",
            tool_name = "Agent",
            tool_input = new { description = "correctness", prompt = Prompt, subagent_type = "review-lens" },
            tool_use_id = ToolUseId,
            tool_response = new
            {
                status = "completed",
                agentId = AgentId,
                content = new[] { new { type = "text", text } },
            },
        });

        public void Stop(string lastAssistantMessage) => Send(new
        {
            hook_event_name = "SubagentStop",
            session_id = Session,
            cwd = _host.RepoRoot,
            agent_id = AgentId,
            agent_type = "review-lens",
            last_assistant_message = lastAssistantMessage,
        });

        private string Send(object payload)
        {
            using StringWriter stdout = new();
            using StringWriter log = new();
            ArbiterHookDecisionEngine engine = _host.Engine(
                providerFactory: _ => _provider,
                gitFactsFactory: _ => new NoGitFacts(),
                planReaderFactory: _ => new NoPlan(),
                homeDirectory: () => _host.RepoRoot);
            HookCommand command = _host.HookCommand(ArbiterTestServices.Registry(engine), configLoader: _ => Config);
            int exit = command.Run("claude", null, JsonSerializer.Serialize(payload), stdout, log);
            Assert.Equal(0, exit);
            _host.Clock.Advance(TimeSpan.FromSeconds(1));
            return log.ToString();
        }

        private sealed class NoGitFacts : IArbiterGitFacts
        {
            public ArbiterFactSet Enrich(
                ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
        }

        private sealed class NoPlan : IArbiterPlanReader
        {
            public ArbiterFactSet Enrich(
                ArbiterFactSet facts, TriggerClassification classification) => facts;
        }
    }
}
