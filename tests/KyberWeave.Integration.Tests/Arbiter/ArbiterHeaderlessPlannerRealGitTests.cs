using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;
using KyberWeave.Tests.Arbiter;

namespace KyberWeave.Integration.Tests.Arbiter;

/// <summary>
/// A Copilot CLI <c>task</c> payload with no routing headers, replayed against a real git checkout: the planner
/// writes its plan after dispatch, and only real git reports that change since the dispatch snapshot.
/// </summary>
public sealed class ArbiterHeaderlessPlannerRealGitTests : IDisposable
{
    private readonly RunStartProbe _probe = RunStartProbe.Create();
    private readonly TempDirectory _home = new();

    public ArbiterHeaderlessPlannerRealGitTests()
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
    public async Task AHeaderlessPlannerThatWroteItsPlan_IsNotEscalatedOnReturn()
    {
        const string Prompt = "Plan the status cards.";
        await Run(Payload("architect", Prompt));
        Directory.CreateDirectory(Path.Combine(_probe.Root, "docs", "plans"));
        await File.WriteAllTextAsync(Path.Combine(_probe.Root, "docs", "plans", "cards.md"), "# Plan\n");

        string stdout = await Run(Payload(
            "architect",
            Prompt,
            objects: new Dictionary<string, object> { ["toolResult"] = new { resultType = "success", textResultForLlm = "Plan written." } }));

        Assert.DoesNotContain("ARBITER_ESCALATION", stdout, StringComparison.Ordinal);
        Assert.Contains(
            Decisions().Where(decision => decision.Trigger == "investigate.returned").SelectMany(decision => decision.Rules),
            rule => rule.Id == "KW-ARB-READONLY-001" && rule.Answer == "not-read-only");
        Assert.DoesNotContain(Decisions().SelectMany(decision => decision.Rules), rule => rule.Effect == "escalate");
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

    private async Task<string> Run(string stdin, IArbiterProvider? provider = null)
    {
        ArbiterHookDecisionEngine engine = ArbiterTestServices.Engine(providerFactory: _ => provider ?? new NoneProvider(), homeDirectory: () => _home.Path);
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(engine),
            ArbiterTestServices.LoadHostConfig,
            () => "cli-1",
            runContexts: ArbiterTestServices.Get<IHookRunContextResolver>());
        using StringWriter stdout = new();
        using StringWriter log = new();

        Assert.Equal(0, await command.RunAsync("copilot-cli", null, stdin, stdout, log, homeDirectory: _home.Path));
        return stdout.ToString();
    }

    private IReadOnlyList<ArbiterDecisionRecord> Decisions() =>
        RealArbiterState.Decisions(Path.Combine(_probe.Root, "artifacts", "arbiter")).ReadAll();
}
