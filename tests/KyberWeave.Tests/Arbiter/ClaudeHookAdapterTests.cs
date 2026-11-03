using System.Collections.Concurrent;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.1: the Claude harness adapter. Covers the deny, allow, strip,
/// post block, annotation and launch-link shapes from the fixture payloads under
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
    }

    private static KyberWeaveConfig EnabledConfig() =>
        new() { Arbiter = new ArbiterConfig { Enabled = true } };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "claude", name));

    private static (int Exit, string Stdout) Run(
        string stdin, string? caller, ScriptedEngine engine)
    {
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(engine),
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
            "\"subagent_type\": \"csharp-dev\"", "\"subagent_type\": \"docs-dev\"", StringComparison.Ordinal);

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
        Assert.Equal("csharp-dev", updated.GetProperty("subagent_type").GetString());
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
    public void PostDispatch_AsyncLaunched_RecordsLinkAndWritesNothing()
    {
        string linkId = "toolu-launch-" + Guid.NewGuid().ToString("N");
        string stdin = Fixture("post-async-launched.json").Replace(
            "toolu-launch-1", linkId, StringComparison.Ordinal);

        (int exit, string stdout) = Run(stdin, "conductor", new ScriptedEngine());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout);
        Assert.True(
            ClaudeHookAdapter.LaunchLinks.TryGetValue(linkId, out string? agentId),
            $"No launch link recorded for tool_use_id '{linkId}'.");
        Assert.Equal("agent-9", agentId);
    }

    [Fact]
    public void Caller_PayloadAgentType_OutranksCallerFlag()
    {
        ScriptedEngine engine = new();

        Run(Fixture("pre-agent-dispatch.json"), "csharp-dev", engine);

        Assert.Equal("conductor", engine.SeenPreCaller);
    }

    [Fact]
    public void Caller_FallsBackToCallerFlagWithoutPayloadAgentType()
    {
        string stdin = Fixture("pre-agent-dispatch.json").Replace(
            "\"agent_type\": \"conductor\",", string.Empty, StringComparison.Ordinal);
        ScriptedEngine engine = new();

        (int exit, _) = Run(stdin, "test-dev", engine);

        Assert.Equal(0, exit);
        Assert.Equal("test-dev", engine.SeenPreCaller);
    }

    [Fact]
    public void LaunchLinks_AreKeyedByToolUseId()
    {
        // The store is keyed by tool_use_id so concurrent background launches do not collide.
        ConcurrentDictionary<string, string> links = ClaudeHookAdapter.LaunchLinks;

        Assert.NotNull(links);
    }
}
