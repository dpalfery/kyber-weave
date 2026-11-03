using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 12.1: the Pi plugin-envelope adapter. Pi dispatches through the
/// <c>Agent</c> tool (from <c>@tintinweb/pi-subagents</c>), with the target at
/// <c>args.subagent_type</c> and the prompt at <c>args.prompt</c>. Both phases answer
/// as task 4.3 defines, and a malformed envelope blocks carrying
/// <c>KW-ARB-HOOK-001</c> with the harness token recorded.
/// RED: no <c>pi</c> adapter is registered in <c>PluginHookAdapters</c>.
/// </summary>
public sealed class PiHookAdapterTests
{
    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public string? SeenPreTarget;

        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config)
        {
            SeenPreTarget = target;
            return PreResult;
        }

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            return PostResult;
        }
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static string Envelope(
        string phase,
        string tool,
        JsonObject args,
        JsonNode? result = null,
        string schema = PluginEnvelope.SchemaV1,
        string harness = "pi")
    {
        JsonObject envelope = new()
        {
            ["schema"] = schema,
            ["harness"] = harness,
            ["phase"] = phase,
            ["tool"] = tool,
            ["call-id"] = "call-1",
            ["session"] = "sess-1",
            ["cwd"] = Directory.GetCurrentDirectory(),
            ["args"] = args,
        };
        if (result is not null)
        {
            envelope["result"] = result;
        }

        return envelope.ToJsonString();
    }

    private static JsonObject AgentArgs(string target, string prompt) => new()
    {
        ["description"] = "Implement T12",
        ["prompt"] = prompt,
        ["subagent_type"] = target,
    };

    private static (int Exit, string Stdout) Run(
        string stdin, string? caller, ScriptedEngine engine)
    {
        PluginHookAdapter adapter = new("pi", "Agent", "subagent_type", "prompt", engine);
        HookCommand command = new(
            new HarnessAdapterRegistry([adapter]),
            _ => EnabledConfig(),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run("pi", caller, stdin, stdout, log);
        return (exit, stdout.ToString());
    }

    [Fact]
    public void Before_AgentEnvelope_ClassifiedBySubagentTypeAndPrompt()
    {
        const string body = "Implement the plan parser.";
        string prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: 12.1\n\n" + body;
        (int exit, string stdout) = Run(
            Envelope("before", "Agent", AgentArgs("csharp-dev", prompt)), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("allow", doc.RootElement.GetProperty("decision").GetString());
        Assert.False(doc.RootElement.TryGetProperty("reason", out _), "Allow must carry no reason.");
        JsonElement args = doc.RootElement.GetProperty("args");
        Assert.Equal("Implement T12", args.GetProperty("description").GetString());
        Assert.Equal("csharp-dev", args.GetProperty("subagent_type").GetString());
        string stripped = args.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal(body, stripped);
        Assert.DoesNotContain("KYBER-ARBITER", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Before_Deny_WritesBlockWithReason()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout) = Run(
            Envelope("before", "Agent", AgentArgs("csharp-dev", "Implement the parser.")),
            "conductor",
            engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("reason").GetString());
        Assert.Equal("csharp-dev", engine.SeenPreTarget);
    }

    [Fact]
    public void After_Allow_WritesAllow()
    {
        JsonObject args = AgentArgs("csharp-dev", "Implement the parser.");
        (int exit, string stdout) = Run(
            Envelope("after", "Agent", args, new JsonObject { ["output"] = "done" }),
            "conductor",
            new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("allow", doc.RootElement.GetProperty("decision").GetString());
        Assert.False(doc.RootElement.TryGetProperty("reason", out _), "Allow must carry no reason.");
    }

    [Fact]
    public void After_NonAllow_WritesBlockWithEnvelopeAsReason()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, envelope) };
        JsonObject args = AgentArgs("csharp-dev", "Implement the parser.");

        (int exit, string stdout) = Run(
            Envelope("after", "Agent", args, new JsonObject { ["output"] = "done" }),
            "conductor",
            engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(envelope, doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void After_Annotation_WritesBlockWithNoteAsReason()
    {
        const string note = "STATUS: ARBITER_ANNOTATION\nFINDING: correctness/null-plan-task";
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostAnnotation, note) };
        JsonObject args = AgentArgs("csharp-dev", "Implement the parser.");

        (int exit, string stdout) = Run(
            Envelope("after", "Agent", args, new JsonObject { ["output"] = "done" }),
            "code-reviewer",
            engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(note, doc.RootElement.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("{oops")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    [InlineData("{\"schema\":\"nope\",\"harness\":\"pi\",\"phase\":\"before\",\"tool\":\"Agent\"}")]
    [InlineData("{\"schema\":\"kyber-arbiter.plugin-event/v1\",\"harness\":\"pi\",\"phase\":\"middle\",\"tool\":\"Agent\"}")]
    public void MalformedEnvelope_BlocksFailClosedWithHarnessToken(string stdin)
    {
        ScriptedEngine engine = new();
        PluginHookAdapter adapter = new("pi", "Agent", "subagent_type", "prompt", engine);
        HookCommand command = new(
            new HarnessAdapterRegistry([adapter]),
            _ => EnabledConfig(),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run("pi", "conductor", stdin, stdout, log);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        string reason = doc.RootElement.GetProperty("reason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("pi", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PiAdapter_IsRegisteredWithDispatchShape()
    {
        ScriptedEngine engine = new();
        IReadOnlyList<IHarnessHookAdapter> adapters = PluginHookAdapters.All(engine);

        IHarnessHookAdapter adapter = Assert.Single(adapters, a =>
            string.Equals(a.HarnessToken, "pi", StringComparison.Ordinal));
        Assert.IsType<PluginHookAdapter>(adapter);

        Assert.True(
            HarnessAdapterRegistry.CreateDefault(engine).TryGet("pi", out _),
            "The default registry must serve the pi harness.");
    }
}
