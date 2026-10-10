using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.3: the OpenCode plugin-envelope adapter. The plugin shim
/// sends a <c>kyber-arbiter.plugin-event/v1</c> envelope and reads back
/// <c>{decision, reason, args}</c>: <c>before</c> gates the dispatch (stripping the
/// header block returns the complete args), <c>after</c> blocks on any non-allow
/// outcome with the envelope or note as reason, and a malformed envelope blocks
/// carrying <c>KW-ARB-HOOK-001</c> with the harness token recorded.
/// RED: <c>PluginEnvelope</c> and <c>PluginHookAdapter</c> do not exist yet.
/// </summary>
public sealed class PluginHookAdapterTests
{
    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public string? SeenPreCaller;

        public string? SeenPreTarget;

        public string? SeenPostCaller;

        public HookOutcome PreResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config)
        {
            SeenPreCaller = caller;
            SeenPreTarget = target;
            return PreResult;
        }

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            SeenPostCaller = caller;
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
        string harness = "opencode")
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

    private static JsonObject TaskArgs(string target, string prompt) => new()
    {
        ["description"] = "Implement T4",
        ["prompt"] = prompt,
        ["subagent_type"] = target,
    };

    private static (int Exit, string Stdout) Run(
        string stdin, string? caller, ScriptedEngine engine)
    {
        PluginHookAdapter adapter = new("opencode", "task", "subagent_type", "prompt", engine);
        HookCommand command = new(
            new HarnessAdapterRegistry([adapter]),
            _ => EnabledConfig(),
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();
        int exit = command.Run("opencode", caller, stdin, stdout, log);
        return (exit, stdout.ToString());
    }

    [Fact]
    public void Before_Allow_SpecialistTarget_StripsHeaderBlockAndReturnsCompleteArgs()
    {
        const string body = "Implement the plan parser.";
        string prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/x.md\nTASK: 4.3\n\n" + body;
        (int exit, string stdout) = Run(
            Envelope("before", "task", TaskArgs("csharp-dev", prompt)), "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("allow", doc.RootElement.GetProperty("decision").GetString());
        Assert.False(doc.RootElement.TryGetProperty("reason", out _), "Allow must carry no reason.");
        JsonElement args = doc.RootElement.GetProperty("args");
        Assert.Equal("Implement T4", args.GetProperty("description").GetString());
        Assert.Equal("csharp-dev", args.GetProperty("subagent_type").GetString());
        string stripped = args.GetProperty("prompt").GetString() ?? string.Empty;
        Assert.Equal(body, stripped);
        Assert.DoesNotContain("KYBER-ARBITER", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void Before_Allow_NonSpecialistTarget_WritesAllowWithoutArgs()
    {
        (int exit, string stdout) = Run(
            Envelope("before", "task", TaskArgs("docs-dev", "Write the docs.")),
            "conductor",
            new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("allow", doc.RootElement.GetProperty("decision").GetString());
        Assert.False(doc.RootElement.TryGetProperty("args", out _), "Unstripped allow must carry no args.");
    }

    [Fact]
    public void Before_Deny_WritesBlockWithReason()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nANSWER: adds-work (step 0)";
        ScriptedEngine engine = new() { PreResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout) = Run(
            Envelope("before", "task", TaskArgs("csharp-dev", "Implement the parser.")),
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
        JsonObject args = TaskArgs("csharp-dev", "Implement the parser.");
        (int exit, string stdout) = Run(
            Envelope("after", "task", args, new JsonObject { ["output"] = "done" }),
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
        JsonObject args = TaskArgs("csharp-dev", "Implement the parser.");

        (int exit, string stdout) = Run(
            Envelope("after", "task", args, new JsonObject { ["output"] = "done" }),
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
        JsonObject args = TaskArgs("csharp-dev", "Implement the parser.");

        (int exit, string stdout) = Run(
            Envelope("after", "task", args, new JsonObject { ["output"] = "done" }),
            "code-reviewer",
            engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Equal(note, doc.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Before_NonDispatchTool_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        PluginHookAdapter adapter = new("opencode", "task", "subagent_type", "prompt", engine);
        HookCommand command = new(
            new HarnessAdapterRegistry([adapter]),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run(
            "opencode",
            "conductor",
            Envelope("before", "read", new JsonObject { ["path"] = "x" }),
            stdout,
            log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Fact]
    public void Before_DispatchWithoutTarget_PassesThroughWithoutLoadingConfig()
    {
        int loads = 0;
        ScriptedEngine engine = new();
        PluginHookAdapter adapter = new("opencode", "task", "subagent_type", "prompt", engine);
        HookCommand command = new(
            new HarnessAdapterRegistry([adapter]),
            (string repoRoot) => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run(
            "opencode",
            "conductor",
            Envelope("before", "task", new JsonObject { ["description"] = "no target" }),
            stdout,
            log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Theory]
    [InlineData("{oops")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    [InlineData("{\"schema\":\"nope\",\"harness\":\"opencode\",\"phase\":\"before\",\"tool\":\"task\"}")]
    [InlineData("{\"schema\":\"kyber-arbiter.plugin-event/v1\",\"harness\":\"opencode\",\"phase\":\"middle\",\"tool\":\"task\"}")]
    public void MalformedEnvelope_BlocksFailClosedWithHarnessToken(string stdin)
    {
        (int exit, string stdout) = Run(stdin, "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        string reason = doc.RootElement.GetProperty("reason").GetString() ?? string.Empty;
        Assert.Contains(HookCommand.FailClosedCode, reason, StringComparison.Ordinal);
        Assert.Contains("opencode", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OpencodeAdapter_IsRegisteredWithDispatchShape()
    {
        ScriptedEngine engine = new();
        IReadOnlyList<IHarnessHookAdapter> adapters = PluginHookAdapters.All(engine);

        IHarnessHookAdapter adapter = Assert.Single(adapters, a =>
            string.Equals(a.HarnessToken, "opencode", StringComparison.Ordinal));
        Assert.IsType<PluginHookAdapter>(adapter);

        Assert.True(
            HarnessAdapterRegistry.CreateDefault(engine).TryGet("opencode", out _),
            "The default registry must serve the opencode harness.");
    }
}
