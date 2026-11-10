using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>Pins the Copilot conductor guard: what it denies, permits, and restates.</summary>
public sealed class ConductorGuardTests
{
    private const string Cwd = "/repo";

    [Theory]
    [InlineData("edit")]
    [InlineData("create")]
    [InlineData("apply_patch")]
    [InlineData("rg")]
    [InlineData("web_fetch")]
    [InlineData("codegraph-codegraph_explore")]
    public void ProjectWorkTools_AreDenied(string tool)
    {
        Assert.Contains(tool, ReasonFor(Event(tool, new { path = "src/a.cs" })), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("ask_user")]
    [InlineData("task_complete")]
    [InlineData("rename_chat")]
    [InlineData("tool_search_tool")]
    [InlineData("kyber-weave-docs_answer_question_from_repo_documentation")]
    [InlineData("kw-docs-docs_look_up_glossary_term_definition")]
    [InlineData("kyber-weave-arbiter_evaluate_agent_dispatch_or_return")]
    public void DelegationAndBookkeepingTools_ArePermittedSilently(string tool)
    {
        Assert.Empty(Run(Event(tool, new { })));
    }

    [Theory]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug a-b")]
    [InlineData("kyber-weave doctor --ensure")]
    [InlineData("kyber-weave doctor --ensure /repo")]
    public void RunStartCommands_ArePermitted(string command)
    {
        Assert.Empty(Run(Event("bash", new { command })));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("kyber-weave doctor --ensure; rm -rf /")]
    [InlineData("kyber-weave run start && cat secrets")]
    [InlineData("kyber-weave run start $(id)")]
    [InlineData("kyber-weave run start `id`")]
    [InlineData("kyber-weave run start | tee x")]
    [InlineData("kyber-weave run start\nrm x")]
    public void OtherCommands_AreDenied(string command)
    {
        Assert.NotEmpty(ReasonFor(Event("bash", new { command })));
    }

    [Theory]
    [InlineData("/home/me/.copilot/agents/conductor/references/plan-path.md")]
    [InlineData("AGENTS.md")]
    [InlineData("/repo/6-Docs/plans/README.md")]
    [InlineData("docs/specs/feature/tasks.md")]
    [InlineData("docs/todo/item.md")]
    public void ContractFiles_ArePermitted(string path)
    {
        Assert.Empty(Run(Event("view", new { path })));
    }

    [Theory]
    [InlineData("src/App.cs")]
    [InlineData("README.md")]
    [InlineData("docs/plans/../../src/notes.md")]
    [InlineData("docs/plans/code.cs")]
    [InlineData("/elsewhere/plans/x.md")]
    public void OtherFiles_AreDenied(string path)
    {
        Assert.NotEmpty(ReasonFor(Event("view", new { path })));
    }

    [Fact]
    public void ArgumentsSentAsAJsonString_AreRead()
    {
        string json = JsonSerializer.Serialize(new
        {
            cwd = Cwd,
            toolName = "bash",
            toolArgs = JsonSerializer.Serialize(new { command = "git status" }),
        });

        Assert.NotEmpty(ReasonFor(json));
    }

    [Fact]
    public void ABatchWithOneBlockedTool_IsDenied()
    {
        string json = JsonSerializer.Serialize(new
        {
            cwd = Cwd,
            toolCalls = new object[]
            {
                new { name = "task", args = new { } },
                new { name = "edit", args = new { path = "a.cs" } },
            },
        });

        Assert.Contains("edit", ReasonFor(json), StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerToolCall_IsPermitted()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, agentId = "a1", toolName = "edit", toolArgs = new { } });

        Assert.Empty(Run(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"toolName\":\"bash\",\"toolArgs\":\"not json\"}")]
    public void UnreadableEvents_AreDenied(string json)
    {
        Assert.NotEmpty(ReasonFor(json));
    }

    [Fact]
    public void AllowedMcpServer_IsPermittedOnlyWhenNamed()
    {
        using StringWriter stdout = new();
        ConductorGuard.Run(Event("operator-channel-send", new { }), stdout, TextWriter.Null, "operator-channel");

        Assert.Empty(stdout.ToString());
        Assert.NotEmpty(ReasonFor(Event("operator-channel-send", new { })));
    }

    [Fact]
    public void ModelFacingPrompt_GetsTheWorkflowAppended()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, transformedPrompt = "Complete the original task." });

        using JsonDocument answer = JsonDocument.Parse(Run(json));
        string modified = answer.RootElement.GetProperty("modifiedTransformedPrompt").GetString()!;

        Assert.StartsWith("Complete the original task.", modified, StringComparison.Ordinal);
        Assert.EndsWith(ConductorGuard.WorkflowReminder, modified, StringComparison.Ordinal);
    }

    private static string Event(string tool, object arguments) =>
        JsonSerializer.Serialize(new { cwd = Cwd, toolName = tool, toolArgs = arguments });

    private static string Run(string json)
    {
        using StringWriter stdout = new();
        Assert.Equal(0, ConductorGuard.Run(json, stdout, TextWriter.Null));
        return stdout.ToString();
    }

    private static string ReasonFor(string json)
    {
        using JsonDocument answer = JsonDocument.Parse(Run(json));
        Assert.Equal("deny", answer.RootElement.GetProperty("permissionDecision").GetString());
        return answer.RootElement.GetProperty("permissionDecisionReason").GetString()!;
    }
}
