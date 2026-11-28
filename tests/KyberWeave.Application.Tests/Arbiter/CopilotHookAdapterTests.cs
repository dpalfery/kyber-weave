using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.2: the Copilot harness adapter. Covers the VS Code Local
/// schema (<c>runSubagent</c> with <c>agentName</c>, an absent <c>agentName</c>
/// meaning the calling agent, <c>hookSpecificOutput</c> answers with the complete
/// <c>updatedInput</c>, the content-keyed Read guard, top-level post blocks) and
/// the Copilot CLI schema (<c>task</c> with <c>toolArgs</c> as a string or an
/// object, an absent target on a marked dispatch, <c>permissionDecision</c> /
/// <c>modifiedArgs</c> / <c>additionalContext</c> answers), schema detection with
/// trusted-decision reuse by <c>tool_use_id</c>, and per-schema fail-closed blocks.
/// Fixture payloads live under <c>Fixtures/arbiter-hooks/copilot-vscode/</c> and
/// <c>Fixtures/arbiter-hooks/copilot-cli/</c>, built from [F2] and [F3] of the
/// task packet.
/// RED: <c>CopilotHookAdapter</c> does not exist yet.
/// </summary>
public sealed class CopilotHookAdapterTests
{

    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public int PreCalls;

        public int PostCalls;

        public string? SeenPreCaller;

        public string? SeenPreTarget;

        public string? SeenPostCaller;

        public bool ThrowOnPre;

        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config)
        {
            PreCalls++;
            SeenPreCaller = caller;
            SeenPreTarget = target;
            if (ThrowOnPre)
            {
                throw new InvalidOperationException("provider is down");
            }

            return PreResult;
        }

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            PostCalls++;
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

    private static KyberWeaveConfig DocsRootConfig() =>
        new()
        {
            Ontology = OntologyConfig.ProductDefaults.WithDocsRoots(["docs"]),
            Arbiter = new ArbiterConfig { Enabled = true },
        };

    private static string VsCodeFixture(string name) =>
        EmbeddedFixtureText.Read(typeof(CopilotHookAdapterTests).Assembly, $"arbiter-hooks/copilot-vscode/{name}");

    private static string CliFixture(string name) =>
        EmbeddedFixtureText.Read(typeof(CopilotHookAdapterTests).Assembly, $"arbiter-hooks/copilot-cli/{name}");

    private static async Task<(int Exit, string Stdout, string Log)> Run(
        string harness,
        string stdin,
        string? caller,
        ScriptedEngine engine,
        Func<string, KyberWeaveConfig>? loader = null)
    {
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            loader ?? (_ => EnabledConfig()),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = await command.RunAsync(harness, caller, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    /// <summary>
    /// Copilot CLI 1.x reports a pre-tool-use event as a `toolCalls` batch with neither `hook_event_name` nor
    /// `toolName`. The adapter must classify the dispatch from the batch rather than throw, which the host would
    /// answer with an allow and no rule evaluation.
    /// </summary>
    [Fact]
    public async Task Cli_PreDispatch_ToolCallsBatch_IsEvaluatedAndDenied()
    {
        const string stdin =
            """
            {
              "sessionId": "805cfa95-b5df-4e3f-8044-3cad0179d3a6",
              "cwd": "/Users/dave/git/personal/factory/hotshot-logistics-demo",
              "toolCalls": [
                {
                  "id": "call_Ts325c8Tu1vdpBpB2jp8ZcpF",
                  "name": "task",
                  "args": {
                    "agent_type": "dev-medium",
                    "name": "dashboard-plan",
                    "prompt": "KYBER-ARBITER: true\nTASK: T1\n\nDo the work."
                  }
                }
              ]
            }
            """;
        const string envelope = "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, string log) = await Run(CopilotHookAdapter.CliToken, stdin, "conductor", engine);

        Assert.Equal(0, exit);
        Assert.Equal(1, engine.PreCalls);
        Assert.Equal("dev-medium", engine.SeenPreTarget);
        Assert.Contains("\"permissionDecision\":\"deny\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-ARB-HOOK-001", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_PreDispatch_Deny_WritesLocalDenyShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-deny-1", StringComparison.Ordinal);

        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.VsCodeToken, stdin, "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, output.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public async Task Local_PreDispatch_Allow_SpecialistTarget_StripsCompleteInput()
    {
        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.VsCodeToken, VsCodeFixture("pre-runsubagent-dispatch.json"), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());

        // updatedInput is the complete tool_input: every original field survives.
        JsonElement updated = output.GetProperty("updatedInput");
        Assert.Equal("Implement T4.2", updated.GetProperty("description").GetString());
        Assert.Equal("dev-medium", updated.GetProperty("agentName").GetString());
        string prompt = updated.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Implement the parser.", prompt);
        Assert.DoesNotContain("KYBER-ARBITER", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_PreDispatch_AbsentAgentName_TargetsCallingAgent()
    {
        ScriptedEngine engine = new();
        string stdin = VsCodeFixture("pre-runsubagent-no-agentname.json").Replace(
            "toolu-copilot-vs-dispatch-2", "toolu-copilot-vs-noagent-1", StringComparison.Ordinal);

        (int exit, _, _) = await Run(CopilotHookAdapter.VsCodeToken, stdin, "dev-medium", engine);

        Assert.Equal(0, exit);
        Assert.Equal("dev-medium", engine.SeenPreTarget);
    }

    /// <summary>
    /// The insertion-only answer (an Arbiter that is off, or a hook that defers) expands a Local dispatch the
    /// way the evaluated path does: an absent <c>agentName</c> makes the rendered caller the target, so the
    /// prompt service is asked for that role rather than for none.
    /// </summary>
    [Fact]
    public void Local_InsertionOnly_AbsentAgentName_UsesTheCallerTarget()
    {
        DispatchPromptQuery? query = null;
        HookContext context = new(
            "/repo",
            () => "decision-test-1",
            TextWriter.Null,
            BuildDispatchPrompt: q =>
            {
                query = q;
                return new DispatchPromptExpansion(true, "<task>the packet</task>", null);
            });
        CopilotHookAdapter adapter = new(new ScriptedEngine(), CopilotHookAdapter.VsCodeToken);
        string stdin = VsCodeFixture("pre-runsubagent-no-agentname.json");

        string? insertion = adapter.RenderPromptInsertion(
            JsonDocument.Parse(stdin).RootElement.Clone(), "dev-medium", context);

        Assert.NotNull(insertion);
        Assert.Equal("dev-medium", query?.Target);
        using JsonDocument document = JsonDocument.Parse(insertion);
        JsonElement output = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        Assert.Equal(
            "<task>the packet</task>\n\nContinue the implementation.",
            output.GetProperty("updatedInput").GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Local_NonDispatchTool_ReadGuardDeniesOnContent_NotName()
    {
        // The tool name is undocumented and ignored: the guard keys on the input
        // content, so an unlisted tool naming a planning path still denies.
        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.VsCodeToken,
            VsCodeFixture("pre-tool-content-denied.json"),
            "dev-medium",
            new ScriptedEngine(),
            _ => DocsRootConfig());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        string reason = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains("Req 25", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_NonDispatchTool_AllowedContent_WritesNothing()
    {
        ScriptedEngine engine = new();
        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.VsCodeToken,
            VsCodeFixture("pre-tool-content-allowed.json"),
            "dev-medium",
            engine,
            _ => DocsRootConfig());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(0, engine.PreCalls);
    }

    [Fact]
    public async Task Local_NonDispatchTool_UnidentifiedCaller_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = await command.RunAsync(
            CopilotHookAdapter.VsCodeToken, null, VsCodeFixture("pre-tool-content-denied.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public async Task Local_PostDispatch_Block_WritesTopLevelBlock()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, envelope) };

        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.VsCodeToken, VsCodeFixture("post-runsubagent-completed.json"), "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Cli_PreDispatch_Deny_WritesPermissionDecisionShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-object.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("permissionDecisionReason").GetString());
        Assert.Equal("dev-medium", engine.SeenPreTarget);
    }

    [Fact]
    public async Task Cli_PreDispatch_StringArgs_Deny_WritesPermissionDecisionShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-string.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("permissionDecisionReason").GetString());
        Assert.Equal("dev-medium", engine.SeenPreTarget);
    }

    [Fact]
    public async Task Cli_PreDispatch_Allow_SpecialistTarget_StripsCompleteModifiedArgs()
    {
        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-object.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("allow", doc.RootElement.GetProperty("permissionDecision").GetString());

        // modifiedArgs is the complete arguments: every original field survives.
        JsonElement modified = doc.RootElement.GetProperty("modifiedArgs");
        Assert.Equal("Implement T4.2", modified.GetProperty("description").GetString());
        Assert.Equal("dev-medium", modified.GetProperty("agent_type").GetString());
        string prompt = modified.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Implement the parser.", prompt);
        Assert.DoesNotContain("KYBER-ARBITER", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_PreDispatch_MarkedDispatchWithoutTarget_CallsEngineWithAbsentTarget()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: undecidable (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-marked-no-target.json"), null, engine);

        // The target fact is absent, so the rules answer undecidable (escalated here).
        Assert.Equal(0, exit);
        Assert.Null(engine.SeenPreTarget);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public async Task Cli_PreDispatch_UnmarkedDispatchWithoutTarget_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = await command.RunAsync(
            CopilotHookAdapter.CliToken, null, CliFixture("pre-task-unmarked-no-target.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
        Assert.Equal(0, engine.PreCalls);
    }

    [Fact]
    public async Task Cli_PreDispatch_NonTaskTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        string stdin = CliFixture("pre-task-object.json").Replace(
            "\"toolName\": \"task\"", "\"toolName\": \"read\"", StringComparison.Ordinal);
        ScriptedEngine engine = new();
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = await command.RunAsync(CopilotHookAdapter.CliToken, null, stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
        Assert.Equal(0, engine.PreCalls);
    }

    [Fact]
    public async Task Cli_PostDispatch_WritesAdditionalContextWithoutCallId()
    {
        const string note = "STATUS: ARBITER_ANNOTATION\nFINDING: correctness/null-plan-task";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, note) };

        // The CLI payload carries no call id: pairing uses pair-digest, so the
        // return still evaluates and answers with additionalContext.
        (int exit, string stdout, _) = await Run(
            CopilotHookAdapter.CliToken, CliFixture("post-task.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(1, engine.PostCalls);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(note, doc.RootElement.GetProperty("additionalContext").GetString());
    }

    /// <summary>An engine that follows background dispatches: it answers a turn's end with a note and keeps one pending.</summary>
    private sealed class BackgroundEngine : IHookDecisionEngine, IBackgroundDispatchEngine, ILostReturnRecovery
    {
        public string? RecoveredAgentId { get; private set; }

        public List<BackgroundDispatch> Tracked { get; } = [];

        public int PostCalls { get; private set; }

        public HookOutcome ReturnResult { get; set; } = new(HookOutcomeKind.Allow);

        public List<string> Pending { get; } = [];

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            PostCalls++;
            return new HookOutcome(HookOutcomeKind.Allow);
        }

        public void TrackBackgroundDispatch(BackgroundDispatch dispatch, HookContext context) => Tracked.Add(dispatch);

        public BackgroundDispatchRecord? FindBackgroundDispatch(string sessionId, string agentId, HookContext context) => null;

        public int RecoverLostReturn(
            string harness,
            string? sessionId,
            IReadOnlyList<string> agentNames,
            HookContext context,
            string? agentId = null,
            string? stopEvent = null)
        {
            RecoveredAgentId = agentId;
            return 0;
        }

        public HookOutcome DecideBackgroundReturn(
            string harness, string sessionId, string agentId, string response, KyberWeaveConfig config, HookContext context) =>
            ReturnResult;

        public IReadOnlyList<string> TakePendingNotes(string sessionId, string caller, HookContext context)
        {
            List<string> notes = [.. Pending];
            Pending.Clear();
            return notes;
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
        /// <inheritdoc/>
        public Task<int> RecoverLostReturnAsync(
            string harness,
            string? sessionId,
            IReadOnlyList<string> agentNames,
            HookContext context,
            string? agentId = null,
            string? stopEvent = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RecoverLostReturn(harness, sessionId, agentNames, context, agentId, stopEvent));
    }

    private static async Task<string> RunCli(IHookDecisionEngine engine, string stdin)
    {
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine), _ => EnabledConfig(), () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        Assert.Equal(0, await command.RunAsync(CopilotHookAdapter.CliToken, null, stdin, stdout, log));
        Assert.DoesNotContain(HookCommand.FailClosedCode, log.ToString(), StringComparison.Ordinal);
        return stdout.ToString();
    }

    private const string BackgroundStartPayload = """
        {
          "sessionId": "c232d9cc-6701-4a48-a2e9-03bfd9f8ccf2",
          "timestamp": 1794839730926,
          "cwd": "/repo",
          "toolCallId": "call_PexVgPs78EWE3aGpkPEV3xAI",
          "toolName": "task",
          "toolArgs": { "description": "Run first probe", "prompt": "Reply with exactly: FIRST TURN DONE", "agent_type": "research-agent", "name": "hook-probe", "mode": "background" },
          "toolResult": {
            "textResultForLlm": "Agent started in background with agent_id: 28ec0b43-93e2-47b0-b3ad-2530846d5dd3. You'll be notified when it completes.",
            "resultType": "success",
            "toolTelemetry": {
              "properties": { "agent_type": "custom-agent", "execution_mode": "background" },
              "restrictedProperties": { "agent_name": "research-agent", "agent_id": "28ec0b43-93e2-47b0-b3ad-2530846d5dd3" }
            }
          }
        }
        """;

    private const string SubagentStopPayload = """
        {
          "sessionId": "c232d9cc-6701-4a48-a2e9-03bfd9f8ccf2",
          "timestamp": 1794839734914,
          "cwd": "/repo",
          "transcriptPath": "/home/.copilot/session-state/c232d9cc-6701-4a48-a2e9-03bfd9f8ccf2/events.jsonl",
          "agentId": "28ec0b43-93e2-47b0-b3ad-2530846d5dd3",
          "agentType": "research-agent",
          "agentName": "research-agent",
          "response": "FIRST TURN DONE",
          "stopReason": "end_turn",
          "stop_hook_active": false,
          "parentAgentId": "c232d9cc-6701-4a48-a2e9-03bfd9f8ccf2"
        }
        """;

    [Fact]
    public async Task Cli_BackgroundStart_IsHandedToTheEngineToFollow_AndIsNotDecidedAsAReturn()
    {
        BackgroundEngine engine = new();

        string stdout = await RunCli(engine, BackgroundStartPayload);

        Assert.Empty(stdout);
        Assert.Equal(0, engine.PostCalls);
        BackgroundDispatch tracked = Assert.Single(engine.Tracked);
        Assert.Equal("28ec0b43-93e2-47b0-b3ad-2530846d5dd3", tracked.AgentId);
        Assert.Equal("c232d9cc-6701-4a48-a2e9-03bfd9f8ccf2", tracked.SessionId);
        Assert.Equal("research-agent", tracked.Target);
        Assert.Equal("Reply with exactly: FIRST TURN DONE", tracked.Prompt);
    }

    [Fact]
    public async Task Cli_BackgroundStart_IsRecognisedFromTelemetryWhenTheResultTextIsElided()
    {
        BackgroundEngine engine = new();

        await RunCli(engine, BackgroundStartPayload.Replace(
            "Agent started in background with agent_id: 28ec0b43-93e2-47b0-b3ad-2530846d5dd3. You'll be notified when it completes.",
            "[copilot:elided textResultForLlm (468 bytes)]",
            StringComparison.Ordinal));

        Assert.Equal("28ec0b43-93e2-47b0-b3ad-2530846d5dd3", Assert.Single(engine.Tracked).AgentId);
        Assert.Equal(0, engine.PostCalls);
    }

    [Fact]
    public async Task Cli_SubagentStop_WithANote_AppendsItToTheResponseTheDispatcherReads()
    {
        BackgroundEngine engine = new() { ReturnResult = new HookOutcome(HookOutcomeKind.PostBlock, "ARBITER_ESCALATION: gate report disagrees") };

        string stdout = await RunCli(engine, SubagentStopPayload);

        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(
            "FIRST TURN DONE\n\nARBITER_ESCALATION: gate report disagrees",
            doc.RootElement.GetProperty("modifiedResponse").GetString());
    }

    [Fact]
    public async Task Cli_SubagentStop_PassesThePayloadAgentIdToRecovery()
    {
        BackgroundEngine engine = new();

        await RunCli(engine, SubagentStopPayload);

        Assert.Equal("28ec0b43-93e2-47b0-b3ad-2530846d5dd3", engine.RecoveredAgentId);
    }

    [Fact]
    public async Task Cli_SubagentStop_WithNothingToSay_WritesNothing()
    {
        Assert.Empty(await RunCli(new BackgroundEngine(), SubagentStopPayload));
    }

    [Fact]
    public async Task Local_SubagentStop_PassesThroughWithoutLoadingConfig()
    {
        // VS Code loads the CLI hook file too, and names the event in its own schema.
        const string Payload = """{"hook_event_name":"SubagentStop","session_id":"s1","cwd":"/repo","agent_id":"a1","agent_type":"dev-medium"}""";
        int loads = 0;

        (int exit, string stdout, string log) = await Run(
            CopilotHookAdapter.CliToken, Payload, null, new ScriptedEngine(), _ => { loads++; return EnabledConfig(); });

        Assert.Equal(0, exit);
        Assert.Empty(stdout);
        Assert.Equal(0, loads);
        Assert.DoesNotContain(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_SubagentStop_PassesThroughForAnEngineThatDoesNotFollowBackgroundDispatches()
    {
        ScriptedEngine engine = new();

        (int exit, string stdout, string log) = await Run(CopilotHookAdapter.CliToken, SubagentStopPayload, null, engine);

        Assert.Equal(0, exit);
        Assert.Empty(stdout);
        Assert.DoesNotContain(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_NextTaskResult_CarriesANoteStillPendingFromABackgroundReturn()
    {
        BackgroundEngine engine = new();
        engine.Pending.Add("Arbiter note on the return of background agent 'dev-medium':\nARBITER_ESCALATION: gate report disagrees");

        string stdout = await RunCli(engine, BackgroundStartPayload);

        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Contains("ARBITER_ESCALATION: gate report disagrees", doc.RootElement.GetProperty("additionalContext").GetString(), StringComparison.Ordinal);
        Assert.Empty(await RunCli(engine, BackgroundStartPayload));
    }

    [Fact]
    public async Task SchemaDetection_LocalPayloadUnderCliHarness_AnswersLocalShapeAndLogsVsCode()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-xharness-1", StringComparison.Ordinal);

        // VS Code also loads the Copilot CLI hook file: a Local-schema payload
        // arriving under --harness copilot-cli answers in the received schema.
        (int exit, string stdout, string log) = await Run(CopilotHookAdapter.CliToken, stdin, "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Contains(CopilotHookAdapter.VsCodeToken, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedDecision_ReusedForSameToolUseId()
    {
        ScriptedEngine engine = new();
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-reuse-1", StringComparison.Ordinal);

        (int firstExit, string firstStdout, _) = await Run(CopilotHookAdapter.VsCodeToken, stdin, "conductor", engine);
        Assert.Equal(0, firstExit);
        Assert.Equal(1, engine.PreCalls);

        // The project-wide hook re-fires for the same tool_use_id without a
        // trusted caller: the logged trusted-caller decision is reused.
        engine.PreResult = new HookOutcome(
            HookOutcomeKind.Deny, "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)");
        (int secondExit, string secondStdout, string secondLog) = await Run(
            CopilotHookAdapter.CliToken, stdin, null, engine);

        Assert.Equal(0, secondExit);
        Assert.Equal(firstStdout, secondStdout);
        Assert.Equal(1, engine.PreCalls);
        Assert.Contains("toolu-copilot-vs-reuse-1", secondLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedDecision_NotReusedWhenThePayloadChangesForTheSameToolUseId()
    {
        ScriptedEngine engine = new();
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-reuse-2", StringComparison.Ordinal);

        (int firstExit, _, _) = await Run(CopilotHookAdapter.VsCodeToken, stdin, "conductor", engine);
        Assert.Equal(0, firstExit);
        Assert.Equal(1, engine.PreCalls);

        string changed = stdin.Replace("Implement the parser.", "Implement a different thing.", StringComparison.Ordinal);
        (int secondExit, _, _) = await Run(CopilotHookAdapter.CliToken, changed, null, engine);

        Assert.Equal(0, secondExit);
        Assert.Equal(2, engine.PreCalls);
    }

    [Fact]
    public async Task TrustedDecision_NotReusedWhenTheWorkingDirectoryChanges()
    {
        ScriptedEngine engine = new();
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-reuse-3", StringComparison.Ordinal);

        (int firstExit, _, _) = await Run(CopilotHookAdapter.VsCodeToken, stdin, "conductor", engine);
        Assert.Equal(0, firstExit);
        Assert.Equal(1, engine.PreCalls);

        string changed = stdin.Replace("\"cwd\": \"/repo\"", "\"cwd\": \"/other\"", StringComparison.Ordinal);
        (int secondExit, _, _) = await Run(CopilotHookAdapter.CliToken, changed, null, engine);

        Assert.Equal(0, secondExit);
        Assert.Equal(2, engine.PreCalls);
    }

    [Fact]
    public async Task InternalError_LocalSchema_AllowsAndLogs()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-error-1", StringComparison.Ordinal);

        (int exit, string stdout, string log) = await Run(CopilotHookAdapter.VsCodeToken, stdin, "conductor", engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalError_CliSchema_AllowsAndLogs()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, string log) = await Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-object.json"), null, engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapters_AreRegisteredForBothTokens()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = ArbiterTestServices.Registry(engine);

        Assert.True(registry.TryGet(CopilotHookAdapter.VsCodeToken, out IHarnessHookAdapter? vscode));
        Assert.True(registry.TryGet(CopilotHookAdapter.CliToken, out IHarnessHookAdapter? cli));
        Assert.IsType<CopilotHookAdapter>(vscode);
        Assert.IsType<CopilotHookAdapter>(cli);
    }
}
