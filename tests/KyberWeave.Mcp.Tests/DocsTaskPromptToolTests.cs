using KyberWeave.Core;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs_build_task_prompt_for_agent_dispatch</c>: the conductor asks which agent to dispatch and gets the
/// agent, the difficulty and its source, the technology and what the budget left out — never the prompt text.
/// </summary>
public sealed class DocsTaskPromptToolTests
{
    [Fact]
    public async Task TheToolNamesTheAgentDifficultyTechnologyAndCutListAndHoldsNoPromptText()
    {
        using DocFixture fixture = await DocsTaskPromptFixture.ReadyAsync();

        string reply = await DocsTaskPromptFixture.Tools(fixture).BuildTaskPromptForClientAsync("claude-code", taskId: "T1");

        Assert.Contains("provenance:", reply, StringComparison.Ordinal);
        Assert.Contains("prompt: built", reply, StringComparison.Ordinal);
        Assert.Contains("agent: ", reply, StringComparison.Ordinal);
        Assert.Contains("difficulty: medium (source: default)", reply, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", reply, StringComparison.Ordinal);
        Assert.Contains("left out: none", reply, StringComparison.Ordinal);
        Assert.Contains("harness: claude (from MCP client 'claude-code')", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARED-RULES-MARKER", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("CSHARP-RULES-MARKER", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client name no probe has verified is not a harness the tool can claim rewrites the dispatch: the prompt is
    /// written to the file, and the reply names the client so a new row can be added from evidence.
    /// </summary>
    [Fact]
    public async Task AnUnverifiedMcpClientGetsThePromptFileAndIsNamedInTheReply()
    {
        using DocFixture fixture = await DocsTaskPromptFixture.ReadyAsync();

        string reply = await DocsTaskPromptFixture.Tools(fixture).BuildTaskPromptForClientAsync("warp", taskId: "T1");

        Assert.Contains("harness: unknown MCP client 'warp'; the prompt file was written", reply, StringComparison.Ordinal);
        Assert.Contains("prompt file: .agent_scratchpad/t1/prompt.md", reply, StringComparison.Ordinal);
        Assert.Contains("read it first", reply, StringComparison.Ordinal);
        Assert.True(fixture.Exists(Path.Combine(ScratchLayout.Root, "t1", "prompt.md")));
    }

    [Fact]
    public async Task AMissingMcpClientNameAlsoGetsThePromptFile()
    {
        using DocFixture fixture = await DocsTaskPromptFixture.ReadyAsync();

        string reply = await DocsTaskPromptFixture.Tools(fixture).BuildTaskPromptForClientAsync(clientName: null, taskId: "T1");

        Assert.Contains("harness: unknown MCP client '(none)'; the prompt file was written", reply, StringComparison.Ordinal);
        Assert.Contains("prompt file: .agent_scratchpad/t1/prompt.md", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheToolTakesATechnologyForADispatchThatBelongsToNoTask()
    {
        using DocFixture fixture = await DocsTaskPromptFixture.ReadyAsync();

        string reply = await DocsTaskPromptFixture.Tools(fixture)
            .BuildTaskPromptForClientAsync("claude-code", taskId: null, technology: "csharp");

        Assert.Contains("prompt: built", reply, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("CSHARP-SKILLS-MARKER", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleTaskFileYieldsNoPromptAndTheRepairCommand()
    {
        using DocFixture fixture = await DocsTaskPromptFixture.ReadyAsync();
        fixture.Write(
            PlanTaskFileProbe.PlanPath,
            PlanTaskFileProbe.PlanMarkdown()
                .Replace("The task file validator", "The renamed validator", StringComparison.Ordinal)
                .Replace(
                    "- **Depends on:** none",
                    "- **Depends on:** none\n- **Technology:** csharp",
                    StringComparison.Ordinal));

        string reply = await DocsTaskPromptFixture.Tools(fixture).BuildTaskPromptForClientAsync("claude-code", taskId: "T1");

        Assert.Contains("prompt: none", reply, StringComparison.Ordinal);
        Assert.Contains("docs tasks " + PlanTaskFileProbe.PlanPath + " --write", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", reply, StringComparison.Ordinal);
    }
}
