using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The serve path's ledger records how the caller was named: everything an
/// <c>arbiter_evaluate_agent_dispatch_or_return</c> client sends is asserted (design §12, R7), so the record
/// carries <c>caller-source: asserted</c>, never <c>harness</c> — no harness hook is
/// involved. The hook path keeps recording <c>harness</c>.
/// </summary>
public sealed class ArbiterServeCallerSourceTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private ArbiterTools CreateTools()
    {
        _ = _host.WriteRepoConfig(
            "arbiter:\n" +
            "  enabled: true\n" +
            "  provider:\n" +
            "    kind: none\n" +
            "  rules:\n" +
            string.Join(
                "\n",
                ArbiterExpectedRuleIds.All.Select(
                    id => $"    - id: {id}\n      enabled: false")) + "\n");

        ArbiterHookDecisionEngine engine = _host.Engine(providerFactory: _ => FakeArbiterProvider.None());
        return _host.Tools(engine);
    }

    [Fact]
    public async Task Evaluate_CallerSuppliedByTheClient_IsRecordedAsAsserted()
    {
        ArbiterTools tools = CreateTools();

        string response = tools.Evaluate(
            "delegate",
            new ArbiterFacts
            {
                Target = "dev-medium",
                PlanFile = "docs/plans/x.md",
                Prompt = "Implement the change.",
            });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);

        IReadOnlyList<ArbiterLedgerEvent> events = _host.Ledger.ReadAll();
        ArbiterLedgerEvent? record = Assert.Single(events);
        Assert.Equal("serve", record.Harness);
        Assert.Equal("conductor", record.Caller);
        Assert.Equal(
            ArbiterCallerSources.Asserted,
            record.CallerSource,
            StringComparer.Ordinal);
    }
}
