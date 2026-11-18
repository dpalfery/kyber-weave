using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Services;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Pins the conductor guard's no-read answer: when it answers an event before any container exists, it answers exactly
/// what the guard answers with every port, and an event that needs a read is left to the guard.
/// </summary>
public sealed class ConductorGuardFastPathTests
{
    private const string Cwd = "/repo";
    private const string CopilotHome = "/home/me/.copilot";

    [Theory]
    [InlineData("edit")]
    [InlineData("rg")]
    [InlineData("task_complete")]
    [InlineData("arbiter_evaluate_agent_dispatch_or_return")]
    [InlineData("kyber-weave-docs_answer_question_from_repo_documentation")]
    public void AnEventDecidedByToolName_IsAnsweredWithoutReadsAndMatchesTheGuard(string tool)
    {
        AssertSameAnswer(Event(tool, new { path = "src/a.cs" }));
    }

    [Theory]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug a-b")]
    [InlineData("kyber-weave doctor --ensure")]
    [InlineData("git status")]
    [InlineData("kyber-weave run start --harness copilot && rm -rf /")]
    public void AShellCallDecidedByTheConductorGrammar_IsAnsweredWithoutReadsAndMatchesTheGuard(string command)
    {
        AssertSameAnswer(Event("bash", new { command }));
    }

    [Fact]
    public void AnEventWithNoToolAndAPrompt_IsAnsweredWithTheWorkflowReminder()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, transformedPrompt = "hi" });

        AssertSameAnswer(json);
        Assert.True(ConductorGuard.TryAnswer(json, TextWriter.Null, TextWriter.Null, null, out _));
    }

    [Fact]
    public void AWorkersCall_IsAnsweredWithNothing()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, agentId = "a1", parentAgentId = "p1", agentType = "dev-medium", toolName = "edit", toolArgs = new { } });

        AssertSameAnswer(json);
    }

    [Fact]
    public void AViewOfAPath_NeedsTheGuardBecauseItReadsTheFileSystem()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, toolName = "view", toolArgs = new { path = "AGENTS.md" } });

        Assert.False(ConductorGuard.TryAnswer(json, TextWriter.Null, TextWriter.Null, null, out _));
    }

    [Fact]
    public void ADispatchOfAFileWritingSpecialist_NeedsTheGuardBecauseItReadsTheRunRecords()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, toolName = "task", toolArgs = new { agent_type = "dev-medium", prompt = "Implement it" } });

        Assert.False(ConductorGuard.TryAnswer(json, TextWriter.Null, TextWriter.Null, null, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{oops")]
    [InlineData("{\"toolName\":\"bash\",\"toolArgs\":\"not json\"}")]
    public void AnUnreadableEvent_IsLeftToTheGuardSoItCanDenyAndLogIt(string json)
    {
        using StringWriter stdout = new();

        Assert.False(ConductorGuard.TryAnswer(json, stdout, TextWriter.Null, null, out _));
        Assert.Equal(string.Empty, stdout.ToString());
    }

    private static void AssertSameAnswer(string json)
    {
        using StringWriter fastStdout = new();
        using StringWriter fastLog = new();
        using StringWriter stdout = new();
        using StringWriter log = new();

        bool answered = ConductorGuard.TryAnswer(json, fastStdout, fastLog, null, out int exit);
        int guardExit = ArbiterTestServices.Get<IConductorGuard>().Run(json, stdout, log, copilotHome: CopilotHome);

        Assert.True(answered);
        Assert.Equal(guardExit, exit);
        Assert.Equal(stdout.ToString(), fastStdout.ToString());
        Assert.Equal(log.ToString(), fastLog.ToString());
    }

    private static string Event(string tool, object arguments) =>
        JsonSerializer.Serialize(new { cwd = Cwd, toolName = tool, toolArgs = arguments });
}
