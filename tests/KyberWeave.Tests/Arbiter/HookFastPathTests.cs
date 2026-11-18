using KyberWeave.Arbiter.Hooks;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>Pins the answers a hook process gives before it builds anything: an event it would pass through or allow without reading a file.</summary>
public sealed class HookFastPathTests
{
    private const string PlainBash =
        "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"ls\"},\"cwd\":\"/repo\",\"session_id\":\"s\"}";

    private const string AgentWithoutTarget =
        "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Agent\",\"tool_input\":{\"prompt\":\"x\"},\"cwd\":\"/repo\",\"session_id\":\"s\"}";

    private const string Dispatch =
        "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Agent\",\"tool_input\":{\"prompt\":\"x\",\"subagent_type\":\"dev-medium\"},\"cwd\":\"/repo\",\"session_id\":\"s\"}";

    [Theory]
    [InlineData(PlainBash)]
    [InlineData(AgentWithoutTarget)]
    public void AnEventTheAdapterPassesThrough_NeedsNoServices(string stdin)
    {
        Assert.True(HookFastPath.IsPassThrough("claude", null, stdin));
    }

    [Fact]
    public void ADispatch_IsLeftToTheHookCommand()
    {
        Assert.False(HookFastPath.IsPassThrough("claude", "conductor", Dispatch));
    }

    [Theory]
    [InlineData("claude", "{oops")]
    [InlineData("claude", "[1, 2]")]
    [InlineData("claude", "")]
    [InlineData("not-a-harness", PlainBash)]
    public void AnUnreadableEventOrAnUnknownHarness_IsLeftToTheHookCommandSoItCanReportIt(string harness, string stdin)
    {
        Assert.False(HookFastPath.IsPassThrough(harness, null, stdin));
    }

    [Theory]
    [InlineData("claude", "{\"tool_name\":\"Bash\",\"cwd\":\"/repo\"}")]
    [InlineData("claude", "{\"agent_id\":\"a1\",\"agent_type\":\"conductor\",\"tool_name\":\"Write\",\"cwd\":\"/repo\",\"tool_input\":{\"file_path\":\"x\"}}")]
    [InlineData("copilot-cli", "{\"toolName\":\"edit\",\"toolArgs\":\"{}\",\"cwd\":\"/repo\"}")]
    public void ARootAgentsToolCall_NeedsNoWorkerGuardServices(string harness, string stdin)
    {
        Assert.False(KyberWeave.Arbiter.Hooks.WorkerGuard.NeedsDecision(harness, stdin));
    }

    [Fact]
    public void ASubagentsFileWrite_NeedsTheWorkerGuard()
    {
        const string stdin =
            "{\"agent_id\":\"a1\",\"agent_type\":\"dev-medium\",\"tool_name\":\"Write\",\"cwd\":\"/repo\",\"tool_input\":{\"file_path\":\"x.cs\"}}";

        Assert.True(KyberWeave.Arbiter.Hooks.WorkerGuard.NeedsDecision("claude", stdin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{oops")]
    public void AnUnreadableToolCall_NeedsTheWorkerGuardSoItCanLogIt(string stdin)
    {
        Assert.True(KyberWeave.Arbiter.Hooks.WorkerGuard.NeedsDecision("claude", stdin));
    }
}
