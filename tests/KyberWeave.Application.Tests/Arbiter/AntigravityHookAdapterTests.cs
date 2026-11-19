using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 16.3: the Antigravity harness adapter. Covers the dispatch
/// gate on <c>PreToolUse</c> with <c>toolCall.name == "invoke_subagent"</c>, where
/// each element of <c>toolCall.args.Subagents</c> is one dispatch (target
/// <c>TypeName</c>, prompt <c>Prompt</c>) and the call is denied when any element
/// escalates; the always-answer shapes (<c>{decision: "deny", reason}</c>, and
/// <c>{}</c> otherwise, never <c>allow</c>); the call id
/// <c>&lt;conversationId&gt;:&lt;stepIdx&gt;</c> and <c>cwd</c> from
/// <c>workspacePaths[0]</c>; the post-dispatch record that writes <c>{}</c>; and
/// the fail-closed deny shape.
/// Fixture payloads live under <c>Fixtures/arbiter-hooks/antigravity/</c>, built
/// from [F10] of the task packet.
/// RED: <c>AntigravityHookAdapter</c> does not exist yet.
/// </summary>
public sealed class AntigravityHookAdapterTests : IDisposable
{
    // Antigravity needs a decision on every pre-tool answer; `{}` is reported as a denial with an empty reason.
    private const string AllowDocument = "{\"decision\":\"allow\"}";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public List<(string? Target, string Prompt)> SeenPre { get; } = [];

        public Queue<HookOutcome> PreResults { get; } = new();

        public bool ThrowOnPre;

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config)
        {
            SeenPre.Add((target, prompt));
            if (ThrowOnPre)
            {
                throw new InvalidOperationException("provider is down");
            }

            return PreResults.Count > 0 ? PreResults.Dequeue() : new HookOutcome(HookOutcomeKind.Allow);
        }

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);
    }

    private sealed class ContextualEngine : IContextualHookDecisionEngine
    {
        public List<(string? Target, string Prompt)> SeenPre { get; } = [];

        public List<string?> SeenPreToolCallIds { get; } = [];

        public List<string?> SeenPreSessionIds { get; } = [];

        public List<string> SeenPreRepoRoots { get; } = [];

        public List<string?> SeenPostToolCallIds { get; } = [];

        public List<string?> SeenPostSessionIds { get; } = [];

        public List<string?> SeenPostTargets { get; } = [];

        public Queue<HookOutcome> PreResults { get; } = new();

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

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
            string? sessionId = null)
        {
            SeenPre.Add((target, prompt));
            SeenPreToolCallIds.Add(toolCallId);
            SeenPreSessionIds.Add(sessionId);
            SeenPreRepoRoots.Add(context.RepoRoot);
            return PreResults.Count > 0 ? PreResults.Dequeue() : new HookOutcome(HookOutcomeKind.Allow);
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
            SeenPostTargets.Add(target);
            SeenPostToolCallIds.Add(toolCallId);
            SeenPostSessionIds.Add(sessionId);
            return new HookOutcome(HookOutcomeKind.Allow);
        }
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static KyberWeaveConfig DisabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = false } };

    private static string Fixture(string name) =>
        EmbeddedFixtureText.Read(typeof(AntigravityHookAdapterTests).Assembly, $"arbiter-hooks/antigravity/{name}");

    private (int Exit, string Stdout, string Log) Run(
        string stdin,
        string? caller,
        IHookDecisionEngine engine,
        Func<string, KyberWeaveConfig>? loader = null)
    {
        HookCommand command = _host.HookCommand(
            _host.Registry(engine),
            loader ?? (_ => EnabledConfig()),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run(AntigravityHookAdapter.Token, caller, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    [Fact]
    public void PreInvoke_EscalatingSubagent_WritesDenyShapeWithEnvelope()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new();
        engine.PreResults.Enqueue(new HookOutcome(HookOutcomeKind.Allow));
        engine.PreResults.Enqueue(new HookOutcome(HookOutcomeKind.Deny, envelope));

        (int exit, string stdout, _) = Run(Fixture("pre-invoke-subagent-multi.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(2, engine.SeenPre.Count);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("reason").GetString());
        Assert.False(doc.RootElement.TryGetProperty("permissionOverrides", out _));
    }

    [Fact]
    public void PreInvoke_EveryElementIsOneDispatch_WithTypeNameAndPrompt()
    {
        ContextualEngine engine = new();

        (int exit, _, _) = Run(Fixture("pre-invoke-subagent-multi.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(2, engine.SeenPre.Count);
        Assert.Equal("dev-medium", engine.SeenPre[0].Target);
        Assert.StartsWith("KYBER-ARBITER: true", engine.SeenPre[0].Prompt, StringComparison.Ordinal);
        Assert.Equal("docs-dev", engine.SeenPre[1].Target);
        Assert.Equal("KYBER-ARBITER: true\n\nDocument the adapter.", engine.SeenPre[1].Prompt);
    }

    [Fact]
    public void PreInvoke_EmptyPrompt_AllowsAndLogs()
    {
        ContextualEngine engine = new();
        JsonObject root = JsonNode.Parse(Payload("PreToolUse", "invoke_subagent", _host.RepoRoot))!.AsObject();
        JsonObject subagent = root["toolCall"]!["args"]!["Subagents"]![0]!.AsObject();
        subagent["Prompt"] = string.Empty;

        (int exit, string stdout, string log) = Run(root.ToJsonString(), null, engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(AllowDocument, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
        Assert.Empty(engine.SeenPre);
    }

    [Fact]
    public void PreInvoke_EmptyTypeName_AllowsAndLogs()
    {
        ContextualEngine engine = new();
        JsonObject root = JsonNode.Parse(Payload("PreToolUse", "invoke_subagent", _host.RepoRoot))!.AsObject();
        JsonObject subagent = root["toolCall"]!["args"]!["Subagents"]![0]!.AsObject();
        subagent["TypeName"] = string.Empty;

        (int exit, string stdout, string log) = Run(root.ToJsonString(), null, engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(AllowDocument, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
        Assert.Empty(engine.SeenPre);
    }

    [Fact]
    public void PreInvoke_MissingTypeName_AllowsWithoutEvaluatingATargetlessDispatch()
    {
        ContextualEngine engine = new();
        JsonObject root = JsonNode.Parse(Payload("PreToolUse", "invoke_subagent", _host.RepoRoot))!.AsObject();
        JsonObject subagent = root["toolCall"]!["args"]!["Subagents"]![0]!.AsObject();
        subagent.Remove("TypeName");

        (int exit, string stdout, string log) = Run(root.ToJsonString(), null, engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(AllowDocument, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
        Assert.Empty(engine.SeenPre);
    }

    [Fact]
    public void PreInvoke_AllAllow_WritesTheExplicitAllow()
    {
        (int exit, string stdout, _) = Run(Fixture("pre-invoke-subagent-multi.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(AllowDocument, stdout);
        Assert.DoesNotContain("overwrite", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledArbiter_AnswersTheDocumentedAllowDocument_NotEmptyOutput()
    {
        // With the Arbiter disabled the host still writes the explicit allow: empty output
        // and {} carry no decision, which Antigravity reports as a denial.
        (int exit, string stdout, string log) =
            Run(Fixture("pre-invoke-subagent.json"), null, new ScriptedEngine(), _ => DisabledConfig());

        Assert.Equal(0, exit);
        Assert.Equal(AllowDocument, stdout);
        Assert.Contains("disabled", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreInvoke_CallIdFromConversationAndStep_AndCwdFromWorkspacePaths()
    {
        ContextualEngine engine = new();

        (int exit, _, _) = Run(Fixture("pre-invoke-subagent.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(["conv-antigravity-1:3"], engine.SeenPreToolCallIds);
        Assert.Equal(["conv-antigravity-1"], engine.SeenPreSessionIds);
        Assert.Equal(["/repo"], engine.SeenPreRepoRoots);
    }

    [Fact]
    public void PreInvoke_NonDispatchTool_WritesTheExplicitAllowWithoutEvaluating()
    {
        ContextualEngine engine = new();

        (int exit, string stdout, _) = Run(Fixture("pre-non-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal(AllowDocument, stdout);
        Assert.Empty(engine.SeenPre);
    }

    [Fact]
    public void PostInvoke_RecordsReturnEvent_AndWritesEmptyObject()
    {
        ContextualEngine engine = new();

        (int exit, string stdout, _) = Run(Fixture("post-invoke-subagent.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout);
        Assert.Equal(["dev-medium"], engine.SeenPostTargets);
        Assert.Equal(["conv-antigravity-3:4"], engine.SeenPostToolCallIds);
        Assert.Equal(["conv-antigravity-3"], engine.SeenPostSessionIds);
    }

    [Fact]
    public void InternalError_AllowsWithTheExplicitAllowAndLogs()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, string log) = Run(Fixture("pre-invoke-subagent.json"), null, engine);

        // ADR 0072: a fault the host cannot decide on allows, with the code in the log.
        Assert.Equal(0, exit);
        Assert.Equal(AllowDocument, stdout);
        Assert.Contains(HookCommand.FailClosedCode, log, StringComparison.Ordinal);
    }

    private static string Payload(string eventName, string tool, string workspace, string? cwd = null)
    {
        JsonObject payload = new()
        {
            ["hook_event_name"] = eventName,
            ["toolCall"] = new JsonObject
            {
                ["name"] = tool,
                ["args"] = new JsonObject
                {
                    ["Subagents"] = new JsonArray(new JsonObject
                    {
                        ["TypeName"] = "dev-medium",
                        ["Prompt"] = "KYBER-ARBITER: true\n\nImplement the change.",
                    }),
                },
            },
            ["stepIdx"] = 1,
            ["conversationId"] = "conv-workspace-1",
            ["workspacePaths"] = new JsonArray(workspace),
            ["transcriptPath"] = "/repo/.antigravity/transcript.json",
            ["modelName"] = "antigravity-test-model",
        };
        if (cwd is not null)
        {
            payload["cwd"] = cwd;
        }

        return payload.ToJsonString();
    }

    private (int Exit, string Stdout, string Log) RunWithLoader(
        string stdin,
        Func<string, KyberWeaveConfig> loader)
    {
        HookCommand command = _host.HookCommand(
            _host.Registry(new ScriptedEngine()),
            loader,
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run(AntigravityHookAdapter.Token, null, stdin, stdout, log);
        return (exit, stdout.ToString(), log.ToString());
    }

    // The workspace config is the only one the loader accepts: any other root (the
    // process directory, a payload cwd) fails the load exactly as a directory without
    // host configuration would, so a fix that loads from anywhere else cannot pass.
    [Fact]
    public void Hook_WorkspaceSuppliesRepoRoot_LoadsTheWorkspaceConfig()
    {
        string workspace = ArbiterTestRoots.Under("workspace");
        _ = _host.WriteRepoConfig("arbiter:\n  enabled: false\n", workspace);

        List<string> roots = [];
        (int exit, string stdout, string log) = RunWithLoader(
            Payload("PreToolUse", "invoke_subagent", workspace),
            root =>
            {
                roots.Add(root);
                return string.Equals(root, workspace, StringComparison.Ordinal)
                    ? DisabledConfig()
                    : throw new InvalidOperationException($"no host configuration at '{root}'");
            });

        Assert.Equal(0, exit);
        Assert.Equal([workspace], roots);
        // Even disabled, Antigravity receives its explicit allow.
        Assert.Equal(AllowDocument, stdout);
        Assert.Contains("Arbiter is disabled", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_WorkspaceTakesPrecedenceOverThePayloadCwd()
    {
        string workspace = ArbiterTestRoots.Under("workspace");
        string cwdDirectory = ArbiterTestRoots.Under("cwd");
        _ = _host.WriteRepoConfig("arbiter:\n  enabled: false\n", workspace);

        List<string> roots = [];
        (int exit, _, string log) = RunWithLoader(
            Payload("PreToolUse", "invoke_subagent", workspace, cwd: cwdDirectory),
            root =>
            {
                roots.Add(root);
                return string.Equals(root, workspace, StringComparison.Ordinal)
                    ? DisabledConfig()
                    : throw new InvalidOperationException($"no host configuration at '{root}'");
            });

        Assert.Equal(0, exit);
        Assert.Equal([workspace], roots);
        Assert.Contains("Arbiter is disabled", log, StringComparison.Ordinal);
    }

    // [F10]: the only documented output is `{}`, so a non-dispatch event must be
    // answered with it before the host loads configuration — a misconfigured matcher
    // must not force a config load, and silence is not an Antigravity allow.
    // A workspace path that names nothing is not a repository root. Returning it
    // verbatim sends every ledger and log path to "" and reads the host
    // configuration from the process directory, which is the one root the payload
    // cwd exists to avoid. HookCommand already guards the payload cwd the same way.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Hook_BlankWorkspace_FallsBackToThePayloadCwd(string workspace)
    {
        string cwdDirectory = ArbiterTestRoots.Under("cwd");

        List<string> roots = [];
        (int exit, _, _) = RunWithLoader(
            Payload("PreToolUse", "invoke_subagent", workspace, cwd: cwdDirectory),
            root =>
            {
                roots.Add(root);
                return EnabledConfig();
            });

        Assert.Equal(0, exit);
        Assert.Equal([cwdDirectory], roots);
    }

    [Fact]
    public void Hook_BlankWorkspaceAndNoCwd_FallsBackToTheProcessDirectory()
    {
        List<string> roots = [];
        (int exit, _, _) = RunWithLoader(
            Payload("PreToolUse", "invoke_subagent", "  "),
            root =>
            {
                roots.Add(root);
                return EnabledConfig();
            });

        Assert.Equal(0, exit);
        Assert.Equal([_host.WorkingDirectory], roots);
    }

    [Fact]
    public void Hook_NonDispatchEvent_AnswersTheExplicitAllowBeforeConfigLoad()
    {
        int loads = 0;

        (int exit, string stdout, _) = RunWithLoader(
            Payload("PreToolUse", "view_file", _host.RepoRoot),
            root => { loads++; return EnabledConfig(); });

        Assert.Equal(0, exit);
        Assert.Equal(AllowDocument, stdout);
        Assert.Equal(0, loads);
    }

    [Fact]
    public void Adapters_AreRegisteredForAntigravityToken()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = _host.Registry(engine);

        Assert.True(registry.TryGet(AntigravityHookAdapter.Token, out IHarnessHookAdapter? adapter));
        Assert.IsType<AntigravityHookAdapter>(adapter);
    }
}
