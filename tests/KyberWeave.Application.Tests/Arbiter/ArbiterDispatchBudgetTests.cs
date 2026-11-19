using KyberWeave.Contracts.Services;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-BUDGET-001</c>: the worker guard counts a developer dispatch's tool calls and minutes, and once
/// either is past <c>arbiter.dispatch-budget</c> it denies the next call with the instruction to return
/// <c>STATUS: BUDGET_EXCEEDED</c>.
/// </summary>
public sealed class ArbiterDispatchBudgetTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Budget_DeniesTheCallAfterTheToolCallLimit()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 60, ToolCalls: 3);

        for (int call = 0; call < 3; call++)
        {
            Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
        }

        string answer = Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("permissionDecision").GetString());
        string reason = document.RootElement.GetProperty("permissionDecisionReason").GetString()!;
        Assert.StartsWith(WorkerGuard.BudgetRuleId, reason, StringComparison.Ordinal);
        Assert.Contains(WorkerGuard.BudgetExceededStatus, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_DeniesTheCallAfterTheTimeLimit()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000, CallMinutes: 15);

        Assert.Empty(Guard("claude", Claude("dev-medium", "a-1"), Start, budget));
        Assert.Empty(Guard("claude", Claude("dev-medium", "a-1"), Start.AddMinutes(14), budget));

        string answer = Guard("claude", Claude("dev-medium", "a-1"), Start.AddMinutes(16), budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void Budget_CountsEachDispatchSeparately()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 60, ToolCalls: 1);

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-2"), Start, budget));
        Assert.NotEmpty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
    }

    [Fact]
    public void Budget_StartsAFreshWindowWhenTheSameSubagentIsResumedForARework()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 60, ToolCalls: 1);

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
        Assert.NotEmpty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddSeconds(10), budget));

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(3), budget));
    }

    [Fact]
    public void Budget_StartsAFreshWindowAfterAnIdleGapLongerThanTheBudget()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000, CallMinutes: 15);

        Assert.Empty(Guard("claude", Claude("dev-medium", "a-1"), Start, budget));
        Assert.Empty(Guard("claude", Claude("dev-medium", "a-1"), Start.AddMinutes(40), budget));
        Assert.Empty(Guard("claude", Claude("dev-medium", "a-1"), Start.AddMinutes(50), budget));
    }

    [Fact]
    public void Budget_AllowsATurnResumedAfterALongIdleGap_BecauseTheBudgetMeasuresOneTurn()
    {
        // A live run: the first turn worked 14:20:25-14:26:01, the agent sat idle, and a rework sent to it at
        // 14:40:12 was denied on its first command for being 19 minutes after the first dispatch.
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 150, CallMinutes: 15);

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(5), budget));
        Assert.Empty(Guard("copilot-cli", TurnEnd("dev-medium", "agent-1"), Start.AddMinutes(6), budget));

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(20), budget));
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(30), budget));
    }

    [Fact]
    public void Budget_StillDeniesASingleTurnThatRunsPastTheTimeLimit()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 150, CallMinutes: 15);

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
        Assert.Empty(Guard("copilot-cli", TurnEnd("dev-medium", "agent-1"), Start.AddMinutes(1), budget));
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(20), budget));
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(30), budget));

        string answer = Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(36), budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Contains(WorkerGuard.BudgetExceededStatus, document.RootElement.GetProperty("permissionDecisionReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TurnEnd_OfOneAgent_LeavesAnotherAgentsBudgetAlone()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 60, ToolCalls: 1);

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
        Assert.Empty(Guard("copilot-cli", TurnEnd("dev-medium", "agent-2"), Start.AddSeconds(1), budget));

        Assert.NotEmpty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddSeconds(2), budget));
        Assert.True(WorkerGuard.NeedsDecision("copilot-cli", TurnEnd("dev-medium", "agent-2")));
    }

    [Fact]
    public void StalledCall_DeniesTheNextCallOnce_AndAllowsTheRetry()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000, CallMinutes: 5);

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(4), budget));

        string answer = Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(10), budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("permissionDecision").GetString());
        string reason = document.RootElement.GetProperty("permissionDecisionReason").GetString()!;
        Assert.StartsWith(WorkerGuard.BudgetRuleId, reason, StringComparison.Ordinal);
        Assert.Contains("NOT VERIFIED:", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerGuard.BudgetExceededStatus, reason, StringComparison.Ordinal);

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(10).AddSeconds(5), budget));
    }

    [Fact]
    public void StalledCall_AllowsALongGapAfterACallThatStartedNoProcess()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000, CallMinutes: 5);

        // A read returns at once, so a gap after it is the model's own turn: no process of the worker's can be hung.
        Assert.Empty(Guard("copilot-cli", CopilotView("dev-medium", "agent-1"), Start, budget));
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(6), budget));
    }

    [Fact]
    public void StalledCall_DeniesTheNextCallOnce_AfterAWaitOnARunningShell()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000, CallMinutes: 5);

        // Copilot's read_bash waits on a shell that is still running, which is where a hung test shows up: the call
        // carries no command, but it is judged like the bash call that started the process.
        Assert.Empty(Guard("copilot-cli", CopilotReadBash("dev-medium", "agent-1"), Start, budget));

        string answer = Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(10), budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Contains("NOT VERIFIED:", document.RootElement.GetProperty("permissionDecisionReason").GetString()!, StringComparison.Ordinal);

        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(10).AddSeconds(5), budget));
    }

    [Fact]
    public void StalledCall_StillDeniesAfterABareTimestampFromABudgetFileWrittenBeforeTheMarker()
    {
        string directory = Path.Combine(_host.HomeDirectory, ".kyber-weave", "arbiter", "budgets");
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000, CallMinutes: 5);
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));

        // Rewrite the file as an older build left it: the header, then a call line with a bare timestamp.
        string file = Assert.Single(_host.Files.EnumerateFiles(directory, "*", recursive: false));
        string[] lines = [.. _host.Files.ReadLines(file)];
        Assert.EndsWith(" p", lines[^1], StringComparison.Ordinal);
        _host.Files.WriteAllText(file, string.Join("\n", [lines[0], lines[^1].Split(' ')[0]]) + "\n");

        string answer = Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(6), budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Contains("NOT VERIFIED:", document.RootElement.GetProperty("permissionDecisionReason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void StalledCall_DeniesOnClaudeToo()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000, CallMinutes: 5);

        Assert.Empty(Guard("claude", Claude("dev-medium", "a-1"), Start, budget));
        string answer = Guard("claude", Claude("dev-medium", "a-1"), Start.AddMinutes(6), budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void StalledCall_ReadsTheShippedLimitFromACounterFileWrittenBeforeIt()
    {
        string directory = Path.Combine(_host.HomeDirectory, ".kyber-weave", "arbiter", "budgets");
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000, CallMinutes: 14);
        Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget));
        string file = Assert.Single(_host.Files.EnumerateFiles(directory, "*", recursive: false));
        string[] lines = [.. _host.Files.ReadLines(file)];
        string[] header = lines[0].Split(' ');
        Assert.Equal("14", header[4]);
        _host.Files.WriteAllText(file, string.Join("\n", [string.Join(' ', header[..4]), .. lines[1..]]) + "\n");

        Assert.NotEmpty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start.AddMinutes(6), budget));
    }

    [Theory]
    [InlineData("code-reviewer")]
    [InlineData("architect")]
    [InlineData("devops")]
    [InlineData("general-purpose")]
    public void Budget_IsNotChargedToReviewersPlannersPublishersOrAgentsOutsideTheSquad(string agentType)
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 1, ToolCalls: 1);

        for (int call = 0; call < 3; call++)
        {
            Assert.Empty(Guard("copilot-cli", Copilot(agentType, "agent-1"), Start.AddMinutes(call * 10), budget));
        }
    }

    [Fact]
    public void Budget_IsOffWhenTheProjectTurnsTheArbiterOff()
    {
        for (int call = 0; call < 3; call++)
        {
            Assert.Empty(Guard("copilot-cli", Copilot("dev-medium", "agent-1"), Start, budget: null));
        }
    }

    [Fact]
    public void Config_ReadsTheDispatchBudgetAndRefusesANonPositiveLimit()
    {
        Assert.Equal(new ArbiterDispatchBudgetConfig(15, 150), ArbiterConfigDefaults.Product.DispatchBudget);

        ArbiterConfig merged = ArbiterConfigLoader.LoadUserConfig(
            "arbiter:\n  dispatch-budget:\n    minutes: 20\n    tool-calls: 90\n");
        Assert.Equal(new ArbiterDispatchBudgetConfig(20, 90), merged.DispatchBudget);

        Assert.ThrowsAny<YamlException>(() => ArbiterConfigLoader.LoadUserConfig(
            "arbiter:\n  dispatch-budget:\n    tool-calls: 0\n"));

        Assert.Equal(5, ArbiterConfigDefaults.Product.DispatchBudget.CallMinutes);
        Assert.Equal(
            8,
            ArbiterConfigLoader.LoadUserConfig("arbiter:\n  dispatch-budget:\n    call-minutes: 8\n").DispatchBudget.CallMinutes);
        Assert.ThrowsAny<YamlException>(() => ArbiterConfigLoader.LoadUserConfig(
            "arbiter:\n  dispatch-budget:\n    call-minutes: 0\n"));
    }

    [Fact]
    public void SeatBudget_DeniesAReadPastTheSeatLimitWithThePartialResultInstruction()
    {
        ArbiterDispatchBudgetConfig seatBudget = new(Minutes: 3, ToolCalls: 2, CallMinutes: 2);

        Assert.Empty(SeatGuard(Copilot("review-lens", "seat-1"), Start, seatBudget));
        Assert.Empty(SeatGuard(Copilot("review-lens", "seat-1"), Start, seatBudget));

        string answer = SeatGuard(Copilot("review-lens", "seat-1"), Start, seatBudget);

        using JsonDocument document = JsonDocument.Parse(answer);
        string reason = document.RootElement.GetProperty("permissionDecisionReason").GetString()!;
        Assert.StartsWith(WorkerGuard.BudgetRuleId, reason, StringComparison.Ordinal);
        Assert.Contains(WorkerGuard.SeatBudgetLine, reason, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerGuard.BudgetExceededStatus, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SeatBudget_StillAllowsTheSeatFileWriteAfterTheLimit()
    {
        ArbiterDispatchBudgetConfig seatBudget = new(Minutes: 3, ToolCalls: 1, CallMinutes: 2);

        Assert.Empty(SeatGuard(Copilot("review-lens", "seat-2"), Start, seatBudget));
        Assert.NotEmpty(SeatGuard(Copilot("review-lens", "seat-2"), Start.AddSeconds(10), seatBudget));

        string write = JsonSerializer.Serialize(new
        {
            cwd = _host.HomeDirectory,
            agentId = "seat-2",
            parentAgentId = "root",
            agentType = "review-lens",
            toolName = "create",
            toolArgs = JsonSerializer.Serialize(new { path = Path.Combine(_host.HomeDirectory, "seats", "correctness.md") }),
        });
        Assert.Empty(SeatGuard(write, Start.AddSeconds(20), seatBudget));
    }

    [Fact]
    public void SeatBudget_UsesTheSeatLimitNotTheDeveloperLimit()
    {
        ArbiterDispatchBudgetConfig seatBudget = new(Minutes: 3, ToolCalls: 1, CallMinutes: 2);

        Assert.Empty(SeatGuard(Copilot("review-triage", "seat-3"), Start, seatBudget));
        Assert.NotEmpty(SeatGuard(Copilot("review-triage", "seat-3"), Start.AddSeconds(10), seatBudget));
        Assert.Empty(SeatGuard(Copilot("dev-medium", "dev-3"), Start, seatBudget));
        Assert.Empty(SeatGuard(Copilot("dev-medium", "dev-3"), Start.AddSeconds(10), seatBudget));
    }

    [Fact]
    public void Config_ReadsTheSeatBudgetAndRefusesANonPositiveLimit()
    {
        Assert.Equal(new ArbiterDispatchBudgetConfig(3, 30, 2), ArbiterConfigDefaults.Product.SeatBudget);

        ArbiterConfig merged = ArbiterConfigLoader.LoadUserConfig(
            "arbiter:\n  seat-budget:\n    minutes: 4\n    tool-calls: 40\n");
        Assert.Equal(new ArbiterDispatchBudgetConfig(4, 40, 2), merged.SeatBudget);
        Assert.Equal(ArbiterConfigDefaults.Product.DispatchBudget, merged.DispatchBudget);

        YamlException refused = Assert.ThrowsAny<YamlException>(() => ArbiterConfigLoader.LoadUserConfig(
            "arbiter:\n  seat-budget:\n    minutes: 0\n"));
        Assert.Contains("arbiter.seat-budget", refused.Message, StringComparison.Ordinal);
    }

    private string SeatGuard(string json, DateTimeOffset now, ArbiterDispatchBudgetConfig seatBudget)
    {
        using StringWriter stdout = new();
        _host.Get<IWorkerGuard>().Run(
            "copilot-cli", json, stdout, TextWriter.Null, _host.HomeDirectory, () => now, _ => ArbiterDispatchBudgetConfig.Defaults, _ => seatBudget);
        return stdout.ToString();
    }

    private string Guard(string harness, string json, DateTimeOffset now, ArbiterDispatchBudgetConfig? budget)
    {
        using StringWriter stdout = new();
        _host.Get<IWorkerGuard>().Run(harness, json, stdout, TextWriter.Null, _host.HomeDirectory, () => now, _ => budget);
        return stdout.ToString();
    }

    private string Copilot(string agentType, string agentId) => JsonSerializer.Serialize(new
    {
        cwd = _host.HomeDirectory,
        agentId,
        parentAgentId = "root",
        agentType,
        toolName = "bash",
        toolArgs = JsonSerializer.Serialize(new { command = "dotnet test" }),
    });

    /// <summary>A read through Copilot CLI's file tool: a call that starts no process.</summary>
    private string CopilotView(string agentType, string agentId) => JsonSerializer.Serialize(new
    {
        cwd = _host.HomeDirectory,
        agentId,
        parentAgentId = "root",
        agentType,
        toolName = "view",
        toolArgs = JsonSerializer.Serialize(new { path = Path.Combine(_host.HomeDirectory, "docs", "README.md") }),
    });

    /// <summary>Copilot CLI's wait on a shell that is still running: the call that hangs when the shell's process does.</summary>
    private string CopilotReadBash(string agentType, string agentId) => JsonSerializer.Serialize(new
    {
        cwd = _host.HomeDirectory,
        agentId,
        parentAgentId = "root",
        agentType,
        toolName = "read_bash",
        toolArgs = JsonSerializer.Serialize(new { shellId = "shell-1", delay = 30 }),
    });

    /// <summary>A Copilot CLI <c>subagentStop</c> event, trimmed from a live run: it ends one turn of the agent.</summary>
    private string TurnEnd(string agentType, string agentId) => JsonSerializer.Serialize(new
    {
        sessionId = "c232d9cc-6701-4a48-a2e9-03bfd9f8ccf2",
        cwd = _host.HomeDirectory,
        agentId,
        agentType,
        agentName = agentType,
        response = "STATUS: PASS",
        stopReason = "end_turn",
        stop_hook_active = false,
        parentAgentId = "c232d9cc-6701-4a48-a2e9-03bfd9f8ccf2",
    });

    private string Claude(string agentType, string agentId) => JsonSerializer.Serialize(new
    {
        cwd = _host.HomeDirectory,
        hook_event_name = "PreToolUse",
        agent_id = agentId,
        agent_type = agentType,
        tool_name = "Bash",
        tool_input = new { command = "dotnet test" },
    });
}
