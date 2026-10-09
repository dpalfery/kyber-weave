using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

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
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "copilot-vscode", name));

    private static string CliFixture(string name) =>
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "copilot-cli", name));

    private static (int Exit, string Stdout, string Log) Run(
        string harness,
        string stdin,
        string? caller,
        ScriptedEngine engine,
        Func<string, KyberWeaveConfig>? loader = null)
    {
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            loader ?? (_ => EnabledConfig()),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run(harness, caller, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    [Fact]
    public void Local_PreDispatch_Deny_WritesLocalDenyShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-deny-1", StringComparison.Ordinal);

        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.VsCodeToken, stdin, "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, output.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void Local_PreDispatch_Allow_SpecialistTarget_StripsCompleteInput()
    {
        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.VsCodeToken, VsCodeFixture("pre-runsubagent-dispatch.json"), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());

        // updatedInput is the complete tool_input: every original field survives.
        JsonElement updated = output.GetProperty("updatedInput");
        Assert.Equal("Implement T4.2", updated.GetProperty("description").GetString());
        Assert.Equal("csharp-dev", updated.GetProperty("agentName").GetString());
        string prompt = updated.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Implement the parser.", prompt);
        Assert.DoesNotContain("KYBER-ARBITER", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN_FILE", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_PreDispatch_AbsentAgentName_TargetsCallingAgent()
    {
        ScriptedEngine engine = new();
        string stdin = VsCodeFixture("pre-runsubagent-no-agentname.json").Replace(
            "toolu-copilot-vs-dispatch-2", "toolu-copilot-vs-noagent-1", StringComparison.Ordinal);

        (int exit, _, _) = Run(CopilotHookAdapter.VsCodeToken, stdin, "csharp-dev", engine);

        Assert.Equal(0, exit);
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
    }

    [Fact]
    public void Local_NonDispatchTool_ReadGuardDeniesOnContent_NotName()
    {
        // The tool name is undocumented and ignored: the guard keys on the input
        // content, so an unlisted tool naming a planning path still denies.
        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.VsCodeToken,
            VsCodeFixture("pre-tool-content-denied.json"),
            "csharp-dev",
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
    public void Local_NonDispatchTool_AllowedContent_WritesNothing()
    {
        ScriptedEngine engine = new();
        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.VsCodeToken,
            VsCodeFixture("pre-tool-content-allowed.json"),
            "csharp-dev",
            engine,
            _ => DocsRootConfig());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(0, engine.PreCalls);
    }

    [Fact]
    public void Local_NonDispatchTool_UnidentifiedCaller_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run(
            CopilotHookAdapter.VsCodeToken, null, VsCodeFixture("pre-tool-content-denied.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void Local_PostDispatch_Block_WritesTopLevelBlock()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, envelope) };

        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.VsCodeToken, VsCodeFixture("post-runsubagent-completed.json"), "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Cli_PreDispatch_Deny_WritesPermissionDecisionShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-object.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("permissionDecisionReason").GetString());
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
    }

    [Fact]
    public void Cli_PreDispatch_StringArgs_Deny_WritesPermissionDecisionShape()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-string.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("permissionDecisionReason").GetString());
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
    }

    [Fact]
    public void Cli_PreDispatch_Allow_SpecialistTarget_StripsCompleteModifiedArgs()
    {
        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-object.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("allow", doc.RootElement.GetProperty("permissionDecision").GetString());

        // modifiedArgs is the complete arguments: every original field survives.
        JsonElement modified = doc.RootElement.GetProperty("modifiedArgs");
        Assert.Equal("Implement T4.2", modified.GetProperty("description").GetString());
        Assert.Equal("csharp-dev", modified.GetProperty("agent_type").GetString());
        string prompt = modified.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal("Implement the parser.", prompt);
        Assert.DoesNotContain("KYBER-ARBITER", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_PreDispatch_MarkedDispatchWithoutTarget_CallsEngineWithAbsentTarget()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: undecidable (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-marked-no-target.json"), null, engine);

        // The target fact is absent, so the rules answer undecidable (escalated here).
        Assert.Equal(0, exit);
        Assert.Null(engine.SeenPreTarget);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("permissionDecisionReason").GetString());
    }

    [Fact]
    public void Cli_PreDispatch_UnmarkedDispatchWithoutTarget_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run(
            CopilotHookAdapter.CliToken, null, CliFixture("pre-task-unmarked-no-target.json"), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
        Assert.Equal(0, engine.PreCalls);
    }

    [Fact]
    public void Cli_PreDispatch_NonTaskTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        string stdin = CliFixture("pre-task-object.json").Replace(
            "\"toolName\": \"task\"", "\"toolName\": \"read\"", StringComparison.Ordinal);
        ScriptedEngine engine = new();
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run(CopilotHookAdapter.CliToken, null, stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
        Assert.Equal(0, engine.PreCalls);
    }

    [Fact]
    public void Cli_PostDispatch_WritesAdditionalContextWithoutCallId()
    {
        const string note = "STATUS: ARBITER_ANNOTATION\nFINDING: correctness/null-plan-task";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, note) };

        // The CLI payload carries no call id: pairing uses pair-digest, so the
        // return still evaluates and answers with additionalContext.
        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.CliToken, CliFixture("post-task.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(1, engine.PostCalls);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal(note, doc.RootElement.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void SchemaDetection_LocalPayloadUnderCliHarness_AnswersLocalShapeAndLogsVsCode()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-xharness-1", StringComparison.Ordinal);

        // VS Code also loads the Copilot CLI hook file: a Local-schema payload
        // arriving under --harness copilot-cli answers in the received schema.
        (int exit, string stdout, string log) = Run(CopilotHookAdapter.CliToken, stdin, "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Contains(CopilotHookAdapter.VsCodeToken, log, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedDecision_ReusedForSameToolUseId()
    {
        ScriptedEngine engine = new();
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-reuse-1", StringComparison.Ordinal);

        (int firstExit, string firstStdout, _) = Run(CopilotHookAdapter.VsCodeToken, stdin, "conductor", engine);
        Assert.Equal(0, firstExit);
        Assert.Equal(1, engine.PreCalls);

        // The project-wide hook re-fires for the same tool_use_id without a
        // trusted caller: the logged trusted-caller decision is reused.
        engine.PreResult = new HookOutcome(
            HookOutcomeKind.Deny, "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)");
        (int secondExit, string secondStdout, string secondLog) = Run(
            CopilotHookAdapter.CliToken, stdin, null, engine);

        Assert.Equal(0, secondExit);
        Assert.Equal(firstStdout, secondStdout);
        Assert.Equal(1, engine.PreCalls);
        Assert.Contains("toolu-copilot-vs-reuse-1", secondLog, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalError_LocalSchema_WritesLocalBlock()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };
        string stdin = VsCodeFixture("pre-runsubagent-dispatch.json").Replace(
            "toolu-copilot-vs-dispatch-1", "toolu-copilot-vs-error-1", StringComparison.Ordinal);

        (int exit, string stdout, _) = Run(CopilotHookAdapter.VsCodeToken, stdin, "conductor", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        string reason = output.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalError_CliSchema_WritesCliBlock()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, _) = Run(
            CopilotHookAdapter.CliToken, CliFixture("pre-task-object.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("permissionDecision").GetString());
        string reason = doc.RootElement.GetProperty("permissionDecisionReason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapters_AreRegisteredForBothTokens()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = HarnessAdapterRegistry.CreateDefault(engine);

        Assert.True(registry.TryGet(CopilotHookAdapter.VsCodeToken, out IHarnessHookAdapter? vscode));
        Assert.True(registry.TryGet(CopilotHookAdapter.CliToken, out IHarnessHookAdapter? cli));
        Assert.IsType<CopilotHookAdapter>(vscode);
        Assert.IsType<CopilotHookAdapter>(cli);
    }
}
