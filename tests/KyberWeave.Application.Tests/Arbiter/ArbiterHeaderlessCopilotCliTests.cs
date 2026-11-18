using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Replays the Copilot CLI <c>task</c> payloads of a live conductor run that sent no routing headers: 16
/// ledger events and no decisions. The gate classifies from the payload instead: the target agent type,
/// looked up in the Squad catalog, and the caller the CLI adds inside a subagent.
/// </summary>
public sealed class ArbiterHeaderlessCopilotCliTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public ArbiterHeaderlessCopilotCliTests()
    {
        _host.Git.CommonDirectory = Path.Combine(_host.RepoRoot, ".git");
        _ = _host.WriteRepoConfig(
            "ontology:\n  docs-root: docs\narbiter:\n  enabled: true\n  provider:\n    kind: none\n");
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    [Fact]
    public void ARootDispatchWithNoHeaders_IsEvaluatedAndAllowed()
    {
        string stdout = Run(Payload("dev-medium", "Add the status cards to the dashboard."));

        Assert.DoesNotContain("\"deny\"", stdout, StringComparison.Ordinal);
        ArbiterDecisionRecord record = Assert.Single(Decisions(), decision => decision.Trigger != "delegate.model");
        Assert.Equal("delegate", record.Trigger);
        Assert.Equal("dev-medium", record.Target);
        Assert.Contains(record.Rules, rule => rule.Id == "KW-ARB-ROSTER-001" && rule.Answer == "no-roster");
        Assert.Contains(record.Rules, rule => rule.Id == "KW-ARB-PLAN-001" && rule.Answer == "unresolved");
        Assert.DoesNotContain(record.Rules, rule => rule.Effect == "escalate");
    }

    [Fact]
    public void ARootDispatchWithNoHeaders_IsAllowedWhenTheModelFaults()
    {
        string stdout = Run(Payload("dev-medium", "Add the status cards to the dashboard."), new FaultingProvider());

        Assert.DoesNotContain("\"deny\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Decisions().SelectMany(decision => decision.Rules), rule => rule.Effect == "escalate");
    }

    [Fact]
    public void ADispatchToAnAgentOutsideTheSquad_StillPasses()
    {
        string stdout = Run(Payload("explore", "Find the dashboard."));

        Assert.Empty(stdout);
        Assert.Empty(Decisions());
    }

    [Fact]
    public void ASubagentCaller_IsReadFromThePayload_AndItsWriterDispatchNeedsARun()
    {
        string stdout = Run(Payload(
            "dev-medium",
            "Fix the parser.",
            new Dictionary<string, string> { ["agentId"] = "a1", ["parentAgentId"] = "p1", ["agentType"] = "bug-crusher" }));

        Assert.Contains("\"deny\"", stdout, StringComparison.Ordinal);
        Assert.Contains("KW-ARB-RUN-001", stdout, StringComparison.Ordinal);
        ArbiterDecisionRecord record = Assert.Single(Decisions());
        Assert.Equal("bug-crusher", record.Caller);
    }

    [Theory]
    [InlineData("dev-medium", "Fix the parser.", "not-read-only")]
    [InlineData("architect", "Plan the parser fix.", "not-read-only")]
    [InlineData("research-agent", "Find the parser.", "changed")]
    [InlineData("Explore", "KYBER-ARBITER: true\n\nMap the repository.", "changed")]
    [InlineData("devops", "RUN-START: true\nkyber-weave run start --harness copilot --type fix --slug a", "changed")]
    public void ReadOnlyRule_HoldsOnlyDispatchesThatPromiseAnUnchangedTree(string target, string prompt, string expected)
    {
        ArbiterRule rule = ArbiterConfigDefaults.Product.Rules.First(
            candidate => string.Equals(candidate.Id, "KW-ARB-READONLY-001", StringComparison.Ordinal));
        ArbiterFactSet facts = TriggerFactBuilder.Build(
                "investigate.returned", target, prompt, "bug-crusher", ArbiterCallerSources.Harness, KyberWeaveConfigDefaults.Product)
            .With("git.changed-paths.since-dispatch", new List<string> { "src/a.ts" }, ArbiterFactLabel.Derived)
            .With("git.changed-paths.attributed", new List<string> { "src/a.ts" }, ArbiterFactLabel.Derived);

        Assert.Equal(expected, RuleEngine.Decide(rule, facts));
    }

    private string Payload(
        string target,
        string prompt,
        Dictionary<string, string>? extra = null)
    {
        Dictionary<string, object> payload = new()
        {
            ["sessionId"] = "s-cli",
            ["timestamp"] = 1_731_400_000_000,
            ["cwd"] = _host.RepoRoot,
            ["toolName"] = "task",
            ["toolArgs"] = JsonSerializer.Serialize(new { agent_type = target, description = "work", prompt }),
        };
        foreach (KeyValuePair<string, string> pair in extra ?? [])
            payload[pair.Key] = pair.Value;
        return JsonSerializer.Serialize(payload);
    }

    private string Run(string stdin, IArbiterProvider? provider = null)
    {
        ArbiterHookDecisionEngine engine = _host.Engine(providerFactory: _ => provider ?? new NoneProvider(), homeDirectory: () => _host.HomeDirectory);
        HookCommand command = _host.HookCommand(_host.Registry(engine), _host.LoadHostConfig, () => "cli-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        Assert.Equal(0, command.Run("copilot-cli", null, stdin, stdout, log, homeDirectory: _host.HomeDirectory));
        return stdout.ToString();
    }

    private sealed class FaultingProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("model unreachable");
    }

    private IReadOnlyList<ArbiterDecisionRecord> Decisions() => _host.Decisions;
}
