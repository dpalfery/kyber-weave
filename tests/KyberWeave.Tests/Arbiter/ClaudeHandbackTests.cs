using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.4: the Claude hand-back hook (Q15 decision, option (a)).
/// A background dispatch returns through <c>PreToolUse</c> on
/// <c>SubagentHandback</c>, keyed by the payload's <c>agent_id</c>, joined to its
/// dispatch through task 4.1's <c>agentId</c> link, and carrying
/// <c>tool_input.message</c> as the output. A non-allow outcome is delivered as
/// <c>allow</c> with the complete input in <c>updatedInput</c> and the envelope
/// or note appended to <c>message</c> after one blank line: the hand-back is
/// never denied. A return with no join is recorded unpaired and allowed. A
/// foreground <c>PostToolUse(Agent)</c> completed for the same <c>agentId</c> is
/// not recorded twice.
/// RED: the adapter has no <c>SubagentHandback</c> path yet.
/// </summary>
public sealed class ClaudeHandbackTests
{
    private sealed class ScriptedEngine : IHookDecisionEngine
    {
        public int PostCalls;

        public string? SeenPostCaller;

        public string? SeenPostTarget;

        public string? SeenPostOutput;

        public HookOutcome PostResult { get; set; } = new(HookOutcomeKind.Allow);

        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config)
        {
            PostCalls++;
            SeenPostCaller = caller;
            SeenPostTarget = target;
            SeenPostOutput = toolOutput;
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

    private static string UniqueAgentId() =>
        "agent-handback-" + Guid.NewGuid().ToString("N");

    private static string HandbackFor(string agentId) =>
        Fixture("pre-subagent-handback.json").Replace(
            "agent-9", agentId, StringComparison.Ordinal);

    private static void SeedLaunchLink(string agentId)
    {
        string toolUseId = "toolu-dispatch-" + Guid.NewGuid().ToString("N");
        ClaudeHookAdapter.LaunchLinks[toolUseId] = agentId;
    }

    [Fact]
    public void Handback_JoinedReturn_RecordsMessageAsPostDispatchOutput()
    {
        string agentId = UniqueAgentId();
        SeedLaunchLink(agentId);
        ScriptedEngine engine = new();

        (int exit, string stdout) = Run(HandbackFor(agentId), "csharp-dev", engine);

        Assert.Equal(0, exit);
        Assert.Equal(1, engine.PostCalls);
        Assert.Equal("csharp-dev", engine.SeenPostCaller);
        Assert.Equal("csharp-dev", engine.SeenPostTarget);
        Assert.Equal(
            "Implemented the plan parser with tests passing.",
            engine.SeenPostOutput);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void Handback_NonAllowOutcome_AppendsEnvelopeAfterBlankLineViaCompleteUpdatedInput()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        const string message = "Implemented the plan parser with tests passing.";
        string agentId = UniqueAgentId();
        SeedLaunchLink(agentId);
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostBlock, envelope) };

        (int exit, string stdout) = Run(HandbackFor(agentId), "csharp-dev", engine);

        Assert.Equal(0, exit);
        Assert.Equal(1, engine.PostCalls);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());

        // updatedInput is the complete input: every original field survives and the
        // envelope is appended to message after one blank line.
        JsonElement updated = output.GetProperty("updatedInput");
        Assert.Equal(message + "\n\n" + envelope, updated.GetProperty("message").GetString());
    }

    [Fact]
    public void Handback_DenyOutcome_IsNeverDenied()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate.returned\nANSWER: adds-work (step 0)";
        string agentId = UniqueAgentId();
        SeedLaunchLink(agentId);
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.Deny, envelope) };

        (int exit, string stdout) = Run(HandbackFor(agentId), "csharp-dev", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        Assert.DoesNotContain("\"deny\"", stdout, StringComparison.Ordinal);
        Assert.False(doc.RootElement.TryGetProperty("decision", out _));
    }

    [Fact]
    public void Handback_AnnotationOutcome_AppendsNoteAfterBlankLine()
    {
        const string note = "STATUS: ARBITER_ANNOTATION\nFINDING: correctness/null-plan-task";
        const string message = "Implemented the plan parser with tests passing.";
        string agentId = UniqueAgentId();
        SeedLaunchLink(agentId);
        ScriptedEngine engine = new() { PostResult = new HookOutcome(HookOutcomeKind.PostAnnotation, note) };

        (int exit, string stdout) = Run(HandbackFor(agentId), "csharp-dev", engine);

        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement output = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("allow", output.GetProperty("permissionDecision").GetString());
        JsonElement updated = output.GetProperty("updatedInput");
        Assert.Equal(message + "\n\n" + note, updated.GetProperty("message").GetString());
    }

    [Fact]
    public void Handback_WithoutJoin_IsRecordedUnpairedAndAllowed()
    {
        string agentId = UniqueAgentId();
        ScriptedEngine engine = new();

        (int exit, string stdout) = Run(HandbackFor(agentId), "csharp-dev", engine);

        Assert.Equal(0, exit);
        Assert.Equal(1, engine.PostCalls);
        Assert.Equal(
            "Implemented the plan parser with tests passing.",
            engine.SeenPostOutput);
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void Handback_ThenForegroundPostCompleted_IsNotRecordedTwice()
    {
        string agentId = UniqueAgentId();
        SeedLaunchLink(agentId);
        ScriptedEngine engine = new();

        (int firstExit, string firstStdout) = Run(HandbackFor(agentId), "csharp-dev", engine);
        string completed = Fixture("post-completed.json").Replace(
            "\"agentId\": \"agent-7\"", "\"agentId\": \"" + agentId + "\"", StringComparison.Ordinal);
        (int secondExit, string secondStdout) = Run(completed, "conductor", engine);

        Assert.Equal(0, firstExit);
        Assert.Equal(0, secondExit);
        Assert.Equal(1, engine.PostCalls);
        Assert.Equal(string.Empty, firstStdout);
        Assert.Equal(string.Empty, secondStdout);
    }

    [Fact]
    public void Handback_IsClassifiedBeforeConfigLoad()
    {
        int loads = 0;
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(new ScriptedEngine()),
            root => { loads++; return EnabledConfig(); },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run("claude", "csharp-dev", HandbackFor(UniqueAgentId()), stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(1, loads);
    }
}
