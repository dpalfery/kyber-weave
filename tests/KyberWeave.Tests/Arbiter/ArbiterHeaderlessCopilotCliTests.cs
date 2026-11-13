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
    private readonly RunStartProbe _probe = RunStartProbe.Create();
    private readonly TempDirectory _home = new();

    public ArbiterHeaderlessCopilotCliTests()
    {
        Directory.CreateDirectory(Path.Combine(_probe.Root, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_probe.Root, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\narbiter:\n  enabled: true\n  provider:\n    kind: none\n");
    }

    public void Dispose()
    {
        _probe.Dispose();
        _home.Dispose();
    }

    [Fact]
    public void ARootDispatchWithNoHeaders_IsEvaluatedAndAllowed()
    {
        string stdout = Run(Payload("csharp-dev", "Add the status cards to the dashboard."));

        Assert.DoesNotContain("\"deny\"", stdout, StringComparison.Ordinal);
        ArbiterDecisionRecord record = Assert.Single(Decisions());
        Assert.Equal("investigate", record.Trigger);
        Assert.Equal("csharp-dev", record.Target);
        Assert.Contains(record.Rules, rule => rule.Id == "KW-ARB-ROSTER-001" && rule.Answer == "no-roster");
        Assert.DoesNotContain(record.Rules, rule => rule.Effect == "escalate");
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
            "typescript-dev",
            "Fix the parser.",
            new Dictionary<string, string> { ["agentId"] = "a1", ["parentAgentId"] = "p1", ["agentType"] = "bug-crusher" }));

        Assert.Contains("\"deny\"", stdout, StringComparison.Ordinal);
        Assert.Contains("KW-ARB-RUN-001", stdout, StringComparison.Ordinal);
        ArbiterDecisionRecord record = Assert.Single(Decisions());
        Assert.Equal("bug-crusher", record.Caller);
    }

    [Fact]
    public void AHeaderlessPlannerThatWroteItsPlan_IsNotEscalatedOnReturn()
    {
        const string Prompt = "Plan the status cards.";
        Run(Payload("architect", Prompt));
        Directory.CreateDirectory(Path.Combine(_probe.Root, "docs", "plans"));
        File.WriteAllText(Path.Combine(_probe.Root, "docs", "plans", "cards.md"), "# Plan\n");

        string stdout = Run(Payload(
            "architect",
            Prompt,
            objects: new Dictionary<string, object> { ["toolResult"] = new { resultType = "success", textResultForLlm = "Plan written." } }));

        Assert.DoesNotContain("ARBITER_ESCALATION", stdout, StringComparison.Ordinal);
        Assert.Contains(
            Decisions().Where(decision => decision.Trigger == "investigate.returned").SelectMany(decision => decision.Rules),
            rule => rule.Id == "KW-ARB-READONLY-001" && rule.Answer == "not-read-only");
        Assert.DoesNotContain(Decisions().SelectMany(decision => decision.Rules), rule => rule.Effect == "escalate");
    }

    [Theory]
    [InlineData("typescript-dev", "Fix the parser.", "not-read-only")]
    [InlineData("architect", "Plan the parser fix.", "not-read-only")]
    [InlineData("research-agent", "Find the parser.", "changed")]
    [InlineData("Explore", "KYBER-ARBITER: true\n\nMap the repository.", "changed")]
    [InlineData("github-devops", "RUN-START: true\nkyber-weave run start --harness copilot --type fix --slug a", "changed")]
    public void ReadOnlyRule_HoldsOnlyDispatchesThatPromiseAnUnchangedTree(string target, string prompt, string expected)
    {
        ArbiterRule rule = ArbiterConfig.ProductDefaults.Rules.First(
            candidate => string.Equals(candidate.Id, "KW-ARB-READONLY-001", StringComparison.Ordinal));
        ArbiterFactSet facts = TriggerFactBuilder.Build(
                "investigate.returned", target, prompt, "bug-crusher", ArbiterCallerSources.Harness, KyberWeaveConfig.ProductDefaults)
            .With("git.changed-paths.since-dispatch", new List<string> { "src/a.ts" }, ArbiterFactLabel.Derived)
            .With("git.changed-paths.attributed", new List<string> { "src/a.ts" }, ArbiterFactLabel.Derived);

        Assert.Equal(expected, RuleEngine.Decide(rule, facts));
    }

    private string Payload(
        string target,
        string prompt,
        Dictionary<string, string>? extra = null,
        Dictionary<string, object>? objects = null)
    {
        Dictionary<string, object> payload = new()
        {
            ["sessionId"] = "s-cli",
            ["timestamp"] = 1_731_400_000_000,
            ["cwd"] = _probe.Root,
            ["toolName"] = "task",
            ["toolArgs"] = JsonSerializer.Serialize(new { agent_type = target, description = "work", prompt }),
        };
        foreach (KeyValuePair<string, string> pair in extra ?? [])
            payload[pair.Key] = pair.Value;
        foreach (KeyValuePair<string, object> pair in objects ?? [])
            payload[pair.Key] = pair.Value;
        return JsonSerializer.Serialize(payload);
    }

    private string Run(string stdin)
    {
        ArbiterHookDecisionEngine engine = new(providerFactory: _ => new NoneProvider(), homeDirectory: () => _home.Path);
        HookCommand command = new(HarnessAdapterRegistry.CreateDefault(engine), Composition.LoadHostConfig, () => "cli-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        Assert.Equal(0, command.Run("copilot-cli", null, stdin, stdout, log, homeDirectory: _home.Path));
        return stdout.ToString();
    }

    private IReadOnlyList<ArbiterDecisionRecord> Decisions() =>
        new DecisionLog(Path.Combine(_probe.Root, "artifacts", "arbiter")).ReadAll();
}
