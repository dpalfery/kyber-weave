using KyberWeave.Data.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The latency an Arbiter decision record reports is the time the provider took on the evaluator's time provider, so a
/// test sets it by advancing a fake clock and no real time passes.
/// </summary>
public sealed class ArbiterEvaluatorLatencyTests
{
    private const string Prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nImplement.";
    private const string Directory = "/repo/artifacts/arbiter";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeArbiterLogStore _log = new();
    private readonly InMemoryFileSystem _files;

    public ArbiterEvaluatorLatencyTests() => _files = new InMemoryFileSystem(_clock);

    [Fact]
    public async Task TheDecisionRecordReportsTheTimeTheProviderTookOnTheClock()
    {
        ArbiterEvaluator evaluator = Evaluator(new SlowProvider(_clock, TimeSpan.FromMilliseconds(250)));

        _ = await evaluator.EvaluateAsync(DelegateEvent(), Config());

        ArbiterProviderRecord provider = Assert.Single(new DecisionLog(Directory, _log).ReadAll()).Provider;
        Assert.Equal(250, provider.LatencyMs);
    }

    [Fact]
    public async Task ARunThatTakesNoTimeReportsZeroLatency()
    {
        ArbiterEvaluator evaluator = Evaluator(new SlowProvider(_clock, TimeSpan.Zero));

        _ = await evaluator.EvaluateAsync(DelegateEvent(), Config());

        Assert.Equal(0, Assert.Single(new DecisionLog(Directory, _log).ReadAll()).Provider.LatencyMs);
    }

    private ArbiterEvaluator Evaluator(IArbiterProvider provider) =>
        new(
            provider,
            _ => null,
            new InFlightLedger(Directory, _log),
            new DecisionLog(Directory, _log),
            new PassThroughGitFacts(),
            new PassThroughPlanReader(),
            new FileArbiterModelTargetsReader(_files),
            new GateReportFacts(
                new InMemoryRepositoryFileStore(_files),
                new FakePathResolver(_files),
                new FakeProcessEnvironment(currentDirectory: "/repo")),
            new FileArbiterStateFactory(_log, _files, _files, new FakePathResolver(_files)),
            _clock,
            new FakeIdGenerator());

    private static KyberWeaveConfig Config() => new()
    {
        Arbiter = new ArbiterConfig
        {
            Enabled = true,
            Rules =
            [
                new ArbiterRule(
                    "KW-ARB-ASK-001",
                    "delegate",
                    "Step-1 question.",
                    ["within-task", "other"],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["within-task"] = RuleEffects.Allow,
                        ["other"] = RuleEffects.Allow,
                    },
                    null,
                    new RuleAsk(
                        "choice",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = "delegation.task" },
                        Criteria: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["within-task"] = "criterion",
                            ["other"] = "other criterion",
                        })),
            ],
        },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private static ArbiterEvent DelegateEvent() => new()
    {
        Harness = "claude",
        Phase = "pre",
        Target = "dev-medium",
        Prompt = Prompt,
        HarnessCaller = "conductor",
        IsDispatch = true,
    };

    private sealed class SlowProvider(FakeTimeProvider clock, TimeSpan takes) : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            clock.Advance(takes);
            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null)
                .ToDictionary(rule => rule.Id, _ => "within-task", StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.13.0", null, RequestSent: true));
        }
    }

    private sealed class PassThroughGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    private sealed class PassThroughPlanReader : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) => facts;
    }
}
