using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The serve path's ledger records how the caller was named: everything an
/// <c>arbiter_evaluate</c> client sends is asserted (design §12, R7), so the record
/// carries <c>caller-source: asserted</c>, never <c>harness</c> — no harness hook is
/// involved. The hook path keeps recording <c>harness</c>.
/// </summary>
public sealed class ArbiterServeCallerSourceTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _repo.Dispose();
        _home.Dispose();
    }

    private sealed class FakeProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            // Provider none semantics: step-1 rules are not evaluated.
            Dictionary<string, string> answers = new(StringComparer.Ordinal);
            foreach (ArbiterRule rule in rules)
            {
                if (rule.Ask is not null)
                {
                    answers[rule.Id] = ArbiterStep1BatchResult.NotEvaluated;
                }
            }

            return Task.FromResult(new ArbiterStep1BatchResult(answers, null, null, false));
        }
    }

    private ArbiterTools CreateTools()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Path, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(_repo.Path, ".kyber-weave", "kyber-weave.yml"),
            "arbiter:\n" +
            "  enabled: true\n" +
            "  provider:\n" +
            "    kind: none\n" +
            "  rules:\n" +
            string.Join(
                "\n",
                ArbiterConfigTests.ExpectedRuleIds.Select(
                    id => $"    - id: {id}\n      enabled: false")) + "\n");

        ArbiterHookDecisionEngine engine = new(
            providerFactory: _ => new FakeProvider(),
            homeDirectory: () => _home.Path);
        return new ArbiterTools(new ArbiterServeContext(
            _repo.Path,
            engine,
            TextWriter.Null,
            Composition.LoadHostConfig,
            _ => true));
    }

    [Fact]
    public async Task Evaluate_CallerSuppliedByTheClient_IsRecordedAsAsserted()
    {
        ArbiterTools tools = CreateTools();

        string response = tools.Evaluate(
            "delegate",
            new ArbiterFacts
            {
                Target = "csharp-dev",
                PlanFile = "docs/plans/x.md",
                Prompt = "Implement the change.",
            });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);

        IReadOnlyList<ArbiterLedgerEvent> events = new InFlightLedger(
            Path.Combine(_repo.Path, "artifacts", "arbiter")).ReadAll();
        ArbiterLedgerEvent? record = Assert.Single(events);
        Assert.Equal("serve", record.Harness);
        Assert.Equal("conductor", record.Caller);
        Assert.Equal(
            ArbiterCallerSources.Asserted,
            record.CallerSource,
            StringComparer.Ordinal);
    }
}
