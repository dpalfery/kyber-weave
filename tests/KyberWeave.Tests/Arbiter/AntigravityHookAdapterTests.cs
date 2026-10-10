using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

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
public sealed class AntigravityHookAdapterTests
{
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
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "antigravity", name));

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
        Assert.Equal("csharp-dev", engine.SeenPre[0].Target);
        Assert.StartsWith("KYBER-ARBITER: true", engine.SeenPre[0].Prompt, StringComparison.Ordinal);
        Assert.Equal("docs-dev", engine.SeenPre[1].Target);
        Assert.Equal("Document the adapter.", engine.SeenPre[1].Prompt);
    }

    [Fact]
    public void PreInvoke_AllAllow_WritesEmptyObjectNeverAllow()
    {
        (int exit, string stdout, _) = Run(Fixture("pre-invoke-subagent-multi.json"), null, new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout);
        Assert.DoesNotContain("allow", stdout, StringComparison.OrdinalIgnoreCase);
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
    public void PreInvoke_NonDispatchTool_WritesEmptyObjectWithoutEvaluating()
    {
        ContextualEngine engine = new();

        (int exit, string stdout, _) = Run(Fixture("pre-non-dispatch.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout);
        Assert.Empty(engine.SeenPre);
    }

    [Fact]
    public void PostInvoke_RecordsReturnEvent_AndWritesEmptyObject()
    {
        ContextualEngine engine = new();

        (int exit, string stdout, _) = Run(Fixture("post-invoke-subagent.json"), null, engine);

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout);
        Assert.Equal(["csharp-dev"], engine.SeenPostTargets);
        Assert.Equal(["conv-antigravity-3:4"], engine.SeenPostToolCallIds);
        Assert.Equal(["conv-antigravity-3"], engine.SeenPostSessionIds);
    }

    [Fact]
    public void InternalError_WritesDenyShapeWithFailClosedEnvelope()
    {
        ScriptedEngine engine = new() { ThrowOnPre = true };

        (int exit, string stdout, _) = Run(Fixture("pre-invoke-subagent.json"), null, engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("deny", doc.RootElement.GetProperty("decision").GetString());
        string reason = doc.RootElement.GetProperty("reason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("ANSWER: error", reason, StringComparison.Ordinal);
        Assert.StartsWith("STATUS: ARBITER_ESCALATION", reason, StringComparison.Ordinal);
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
                        ["TypeName"] = "csharp-dev",
                        ["Prompt"] = "Implement the change.",
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

    private static (int Exit, string Stdout, string Log) RunWithLoader(
        string stdin,
        Func<string, KyberWeaveConfig> loader)
    {
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(new ScriptedEngine()),
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
        using TempDirectory workspace = new();
        Directory.CreateDirectory(System.IO.Path.Combine(workspace.Path, ".kyber-weave"));
        File.WriteAllText(
            System.IO.Path.Combine(workspace.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n  enabled: false\n");

        List<string> roots = [];
        (int exit, string stdout, string log) = RunWithLoader(
            Payload("PreToolUse", "invoke_subagent", workspace.Path),
            root =>
            {
                roots.Add(root);
                return string.Equals(root, workspace.Path, StringComparison.Ordinal)
                    ? DisabledConfig()
                    : throw new InvalidOperationException($"no host configuration at '{root}'");
            });

        Assert.Equal(0, exit);
        Assert.Equal([workspace.Path], roots);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Arbiter is disabled", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_WorkspaceTakesPrecedenceOverThePayloadCwd()
    {
        using TempDirectory workspace = new();
        using TempDirectory cwdDirectory = new();
        Directory.CreateDirectory(System.IO.Path.Combine(workspace.Path, ".kyber-weave"));
        File.WriteAllText(
            System.IO.Path.Combine(workspace.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n  enabled: false\n");

        List<string> roots = [];
        (int exit, _, string log) = RunWithLoader(
            Payload("PreToolUse", "invoke_subagent", workspace.Path, cwd: cwdDirectory.Path),
            root =>
            {
                roots.Add(root);
                return string.Equals(root, workspace.Path, StringComparison.Ordinal)
                    ? DisabledConfig()
                    : throw new InvalidOperationException($"no host configuration at '{root}'");
            });

        Assert.Equal(0, exit);
        Assert.Equal([workspace.Path], roots);
        Assert.Contains("Arbiter is disabled", log, StringComparison.Ordinal);
    }

    // [F10]: the only documented output is `{}`, so a non-dispatch event must be
    // answered with it before the host loads configuration — a misconfigured matcher
    // must not force a config load, and silence is not an Antigravity allow.
    [Fact]
    public void Hook_NonDispatchEvent_AnswersEmptyDocumentBeforeConfigLoad()
    {
        int loads = 0;

        (int exit, string stdout, _) = RunWithLoader(
            Payload("PreToolUse", "view_file", "/repo"),
            root => { loads++; return EnabledConfig(); });

        Assert.Equal(0, exit);
        Assert.Equal("{}", stdout);
        Assert.Equal(0, loads);
    }

    [Fact]
    public void Adapters_AreRegisteredForAntigravityToken()
    {
        ScriptedEngine engine = new();
        HarnessAdapterRegistry registry = HarnessAdapterRegistry.CreateDefault(engine);

        Assert.True(registry.TryGet(AntigravityHookAdapter.Token, out IHarnessHookAdapter? adapter));
        Assert.IsType<AntigravityHookAdapter>(adapter);
    }
}
