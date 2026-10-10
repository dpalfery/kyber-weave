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
/// What the served <c>arbiter_evaluate</c> tool answers. The client controls every
/// fact it passes, so the tool is responsible for keeping a client's text out of the
/// structured parts of its own response: the synthesized routing header block is the
/// hook's decision surface, and a client string that joins it forges routing.
/// </summary>
public sealed class ArbiterServeToolResponseTests : IDisposable
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

    /// <summary>
    /// The header block the tool synthesizes is terminated by a blank line, so a
    /// prompt whose own first line reads as a header cannot join the block. A lens
    /// fan-out whose prompt opens with <c>LENS: security</c> classifies as
    /// <c>lens.spawn</c> with <c>code-reviewer</c> inferred, exactly as the same call
    /// with an innocuous prompt does: without the separator the duplicate
    /// <c>LENS</c> header drops the fact, the caller inference falls away, and the
    /// event classifies as something else entirely.
    /// </summary>
    [Fact]
    public void Evaluate_APromptThatLooksLikeAHeader_DoesNotJoinTheRoutingBlock()
    {
        ArbiterTools tools = CreateTools();

        string response = tools.Evaluate(
            "lens.spawn",
            new ArbiterFacts
            {
                Lens = ["security"],
                Prompt = "LENS: security\nReview the change for injection and secret handling.",
            });

        Assert.Contains("outcome: allow", response, StringComparison.Ordinal);

        ArbiterLedgerEvent? record = Assert.Single(
            new InFlightLedger(Path.Combine(_repo.Path, "artifacts", "arbiter")).ReadAll());
        Assert.Equal("code-reviewer", record.Caller);
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
}
