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
    public void TheToolNamesTheAgentDifficultyTechnologyAndCutListAndHoldsNoPromptText()
    {
        using DocFixture fixture = DocsTaskPromptFixture.Ready();

        string reply = DocsTaskPromptFixture.Tools(fixture).BuildTaskPrompt("T1");

        Assert.Contains("provenance:", reply, StringComparison.Ordinal);
        Assert.Contains("prompt: built", reply, StringComparison.Ordinal);
        Assert.Contains("agent: ", reply, StringComparison.Ordinal);
        Assert.Contains("difficulty: medium (source: default)", reply, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", reply, StringComparison.Ordinal);
        Assert.Contains("left out: none", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARED-RULES-MARKER", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("CSHARP-RULES-MARKER", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolTakesATechnologyForADispatchThatBelongsToNoTask()
    {
        using DocFixture fixture = DocsTaskPromptFixture.Ready();

        string reply = DocsTaskPromptFixture.Tools(fixture).BuildTaskPrompt(taskId: null, technology: "csharp");

        Assert.Contains("prompt: built", reply, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("CSHARP-SKILLS-MARKER", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleTaskFileYieldsNoPromptAndTheRepairCommand()
    {
        using DocFixture fixture = DocsTaskPromptFixture.Ready();
        fixture.Write(
            PlanTaskFileProbe.PlanPath,
            PlanTaskFileProbe.PlanMarkdown()
                .Replace("The task file validator", "The renamed validator", StringComparison.Ordinal)
                .Replace(
                    "- **Depends on:** none",
                    "- **Depends on:** none\n- **Technology:** csharp",
                    StringComparison.Ordinal));

        string reply = DocsTaskPromptFixture.Tools(fixture).BuildTaskPrompt("T1");

        Assert.Contains("prompt: none", reply, StringComparison.Ordinal);
        Assert.Contains("docs tasks " + PlanTaskFileProbe.PlanPath + " --write", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<task>", reply, StringComparison.Ordinal);
    }
}
