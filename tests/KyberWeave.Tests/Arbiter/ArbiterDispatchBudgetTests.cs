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

    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

    [Fact]
    public void Budget_DeniesTheCallAfterTheToolCallLimit()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 60, ToolCalls: 3);

        for (int call = 0; call < 3; call++)
        {
            Assert.Empty(Guard("copilot-cli", Copilot("dal-dev", "agent-1"), Start, budget));
        }

        string answer = Guard("copilot-cli", Copilot("dal-dev", "agent-1"), Start, budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("permissionDecision").GetString());
        string reason = document.RootElement.GetProperty("permissionDecisionReason").GetString()!;
        Assert.StartsWith(WorkerGuard.BudgetRuleId, reason, StringComparison.Ordinal);
        Assert.Contains(WorkerGuard.BudgetExceededStatus, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_DeniesTheCallAfterTheTimeLimit()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 15, ToolCalls: 1000);

        Assert.Empty(Guard("claude", Claude("csharp-dev", "a-1"), Start, budget));
        Assert.Empty(Guard("claude", Claude("csharp-dev", "a-1"), Start.AddMinutes(14), budget));

        string answer = Guard("claude", Claude("csharp-dev", "a-1"), Start.AddMinutes(16), budget);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void Budget_CountsEachDispatchSeparately()
    {
        ArbiterDispatchBudgetConfig budget = new(Minutes: 60, ToolCalls: 1);

        Assert.Empty(Guard("copilot-cli", Copilot("dal-dev", "agent-1"), Start, budget));
        Assert.Empty(Guard("copilot-cli", Copilot("dal-dev", "agent-2"), Start, budget));
        Assert.NotEmpty(Guard("copilot-cli", Copilot("dal-dev", "agent-1"), Start, budget));
    }

    [Theory]
    [InlineData("code-reviewer")]
    [InlineData("architect")]
    [InlineData("github-devops")]
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
            Assert.Empty(Guard("copilot-cli", Copilot("dal-dev", "agent-1"), Start, budget: null));
        }
    }

    [Fact]
    public void Config_ReadsTheDispatchBudgetAndRefusesANonPositiveLimit()
    {
        Assert.Equal(new ArbiterDispatchBudgetConfig(15, 150), ArbiterConfig.ProductDefaults.DispatchBudget);

        ArbiterConfig merged = ArbiterConfigLoader.LoadUserConfig(
            "arbiter:\n  dispatch-budget:\n    minutes: 20\n    tool-calls: 90\n");
        Assert.Equal(new ArbiterDispatchBudgetConfig(20, 90), merged.DispatchBudget);

        Assert.ThrowsAny<YamlException>(() => ArbiterConfigLoader.LoadUserConfig(
            "arbiter:\n  dispatch-budget:\n    tool-calls: 0\n"));
    }

    private string Guard(string harness, string json, DateTimeOffset now, ArbiterDispatchBudgetConfig? budget)
    {
        using StringWriter stdout = new();
        WorkerGuard.Run(harness, json, stdout, TextWriter.Null, _home.Path, () => now, _ => budget);
        return stdout.ToString();
    }

    private string Copilot(string agentType, string agentId) => JsonSerializer.Serialize(new
    {
        cwd = _home.Path,
        agentId,
        parentAgentId = "root",
        agentType,
        toolName = "bash",
        toolArgs = JsonSerializer.Serialize(new { command = "dotnet test" }),
    });

    private string Claude(string agentType, string agentId) => JsonSerializer.Serialize(new
    {
        cwd = _home.Path,
        hook_event_name = "PreToolUse",
        agent_id = agentId,
        agent_type = agentType,
        tool_name = "Bash",
        tool_input = new { command = "dotnet test" },
    });
}
