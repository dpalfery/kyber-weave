using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The adapter boundary for the hook host: each harness payload becomes the typed event the guards and rules read.
/// The Copilot fixture is a real <c>hook.start</c> input from a Copilot CLI session.
/// </summary>
public sealed class HarnessHookEventReaderTests
{
    [Fact]
    public void AClaudeDispatch_ReadsTheDirsCallIdentityAndPrompt()
    {
        const string payload =
            """
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Task",
              "tool_use_id": "toolu_01abc",
              "session_id": "session-1",
              "cwd": "/repo",
              "agent_type": "conductor",
              "tool_input": {
                "subagent_type": "dev-medium",
                "prompt": "TASK: T1\nPLAN_FILE: docs/plans/2026-11-18-run.md\n\nDo the work."
              }
            }
            """;

        HookEvent hookEvent = Read("claude", payload);

        Assert.Equal("claude", hookEvent.Harness);
        Assert.Equal("/repo", hookEvent.Cwd);
        Assert.Equal("conductor", hookEvent.AgentType);
        Assert.Equal("Task", hookEvent.ToolName);
        Assert.Equal("toolu_01abc", hookEvent.CallId);
        Assert.Equal("session-1", hookEvent.SessionId);
        Assert.StartsWith("TASK: T1", hookEvent.Prompt, StringComparison.Ordinal);
        Assert.Equal("dev-medium", hookEvent.Arguments.GetProperty("subagent_type").GetString());
    }

    [Fact]
    public void ACopilotCliToolCallBatch_ReadsTheFirstCallAndItsPrompt()
    {
        // Trimmed from a real Copilot CLI session's hook.start input: the pre-tool-use event carries toolCalls.
        const string payload =
            """
            {
              "sessionId": "805cfa95-b5df-4e3f-8044-3cad0179d3a6",
              "cwd": "/Users/dave/git/personal/factory/hotshot-logistics-demo",
              "toolCalls": [
                {
                  "id": "call_Ts325c8Tu1vdpBpB2jp8ZcpF",
                  "name": "task",
                  "args": {
                    "agent_type": "architect",
                    "name": "dashboard-plan",
                    "description": "Plan job status dashboard",
                    "mode": "sync",
                    "prompt": "KYBER-ARBITER: true\nPLAN_FILE: 6-Docs/plans/job-status-dashboard.md\nCreate and validate a Draft delivery plan for the user's open feature request in the run worktree /Users/dave/git/personal/factory/hotshot-logistics-demo.worktrees/20261119T005033Z-copilot-feat-job-status-dashboard."
                  }
                }
              ]
            }
            """;

        HookEvent hookEvent = Read("copilot-cli", payload, renderedCaller: "conductor");

        Assert.Equal("copilot-cli", hookEvent.Harness);
        Assert.Equal("/Users/dave/git/personal/factory/hotshot-logistics-demo", hookEvent.Cwd);
        Assert.Equal("conductor", hookEvent.AgentType);
        Assert.Equal("task", hookEvent.ToolName);
        Assert.Equal("call_Ts325c8Tu1vdpBpB2jp8ZcpF", hookEvent.CallId);
        Assert.Equal("805cfa95-b5df-4e3f-8044-3cad0179d3a6", hookEvent.SessionId);
        Assert.StartsWith("KYBER-ARBITER: true", hookEvent.Prompt, StringComparison.Ordinal);
        Assert.Contains("PLAN_FILE: 6-Docs/plans/job-status-dashboard.md", hookEvent.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ACopilotCliPostEvent_KeepsTheToolCallIdThatPairsWithItsDispatch()
    {
        const string payload =
            """
            {
              "sessionId": "s",
              "cwd": "/repo",
              "toolCallId": "call_1",
              "toolName": "bash",
              "toolArgs": { "command": "kyber-weave run start --harness copilot" },
              "toolResult": { "resultType": "success" }
            }
            """;

        HookEvent hookEvent = Read("copilot-cli", payload);

        Assert.Equal("call_1", hookEvent.CallId);
        Assert.Equal("bash", hookEvent.ToolName);
        Assert.Null(hookEvent.Prompt);
        Assert.Equal("kyber-weave run start --harness copilot", hookEvent.Arguments.GetProperty("command").GetString());
    }

    [Fact]
    public void APluginEnvelope_ReadsItsCwdToolAndPrompt()
    {
        const string payload =
            """
            {
              "schema": "kyber-arbiter.plugin-event/v1",
              "harness": "opencode",
              "phase": "before",
              "tool": "task",
              "call-id": "call-9",
              "session": "ses-1",
              "cwd": "/repo",
              "args": { "subagent_type": "dev-medium", "prompt": "TASK: T1" }
            }
            """;

        HookEvent hookEvent = Read("opencode", payload);

        Assert.Equal("/repo", hookEvent.Cwd);
        Assert.Equal("task", hookEvent.ToolName);
        Assert.Equal("call-9", hookEvent.CallId);
        Assert.Equal("ses-1", hookEvent.SessionId);
        Assert.Equal("TASK: T1", hookEvent.Prompt);
    }

    [Fact]
    public void ACursorDispatchWithNoCwd_TakesItsWorkspaceRoot()
    {
        // Cursor 2026.11 sends workspace_roots and no cwd; the user-level hook runs from ~/.cursor.
        const string payload = """
            { "hook_event_name": "preToolUse", "tool_name": "Task", "workspace_roots": ["/repo"],
              "tool_input": { "subagent_type": "explore", "prompt": "TASK: T1" } }
            """;

        HookEvent hookEvent = Read("cursor", payload);

        Assert.Equal("/repo", hookEvent.WorkspaceRoot);
        Assert.Null(hookEvent.Cwd);
    }

    [Fact]
    public void ABlankWorkspace_IsNoWorkspaceSoTheCwdStands()
    {
        const string payload = """{ "workspacePaths": ["   "], "cwd": "/repo", "tool_name": "view" }""";

        HookEvent hookEvent = Read("antigravity", payload);

        Assert.Null(hookEvent.WorkspaceRoot);
        Assert.Equal("/repo", hookEvent.Cwd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"agent_type\":")]
    public void ToolArgsThatAreNotJson_ReadAsNoArguments(string toolArgs)
    {
        string payload = JsonSerializer.Serialize(new { sessionId = "s1", cwd = "/repo", toolName = "task", toolArgs });

        HookEvent hookEvent = Read("copilot-cli", payload);

        Assert.Equal("/repo", hookEvent.Cwd);
        Assert.Null(hookEvent.Prompt);
        Assert.Equal(JsonValueKind.Undefined, hookEvent.Arguments.ValueKind);
    }

    private static HookEvent Read(string harness, string payload, string? renderedCaller = null)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return HarnessHookEventReader.Read(harness, document.RootElement, renderedCaller);
    }
}
