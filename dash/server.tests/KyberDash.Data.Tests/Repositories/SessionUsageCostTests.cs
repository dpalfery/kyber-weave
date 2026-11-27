using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Harnesses.Droid;
using KyberDash.Data.Harnesses.Warp;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// A session whose records carry no tokens is priced from the usage the harness reported for the
/// whole session: each model's reported totals at that model's published rate, with published
/// basis. No rate for a model, or a total-only figure for a model whose rates differ by class, is
/// not measured with a reason; no rate is derived. Both stores answer the same figures.
/// </summary>
public sealed class SessionUsageCostTests : IDisposable
{
    private const string DroidHarness = DroidSessionMapper.DroidHarnessId;
    private const string SessionKey = "sess-usage-cost";
    private const string ModelA = "model-a";
    private const string ModelB = "model-b";

    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory directory = new();

    /// <summary>Both stores the dashboard reads costs from.</summary>
    public static TheoryData<string> Stores => new() { "sqlite", "memory" };

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Deletes the scratch directory when disposing.</summary>
    /// <param name="disposing">Whether managed state should be released.</param>
    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            directory.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void ReportedUsage_IsPricedPerModelAtItsPublishedRate(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [
                new CollectedModelUsage(0, ModelA, TokenClass.Input, 1000),
                new CollectedModelUsage(0, ModelA, TokenClass.CacheRead, 5000),
                new CollectedModelUsage(0, ModelA, TokenClass.CacheWrite, 200),
                new CollectedModelUsage(0, ModelA, TokenClass.Output, 300),
                new CollectedModelUsage(0, ModelB, TokenClass.Input, 2000),
                new CollectedModelUsage(0, ModelB, TokenClass.Output, 100),
            ]);

        // model-a: 1000*2e-6 + 300*8e-6 + 200*3e-6 + 5000*0.2e-6 = 0.006; model-b: 2000*1e-6 + 100*4e-6 = 0.0024.
        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey, Rates());

        Assert.NotNull(summary);
        AssertPriced(summary.PublishedCost, 0.0084, (ModelA, 0.006), (ModelB, 0.0024));

        SessionListFigure listFigure = Assert.Single(reader.SessionListFigures(DroidHarness, Rates()));
        AssertPriced(listFigure.PublishedCost, 0.0084, (ModelA, 0.006), (ModelB, 0.0024));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void ModelWithoutARate_IsNotMeasuredWithAReason(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [
                new CollectedModelUsage(0, "model-without-rate", TokenClass.Input, 1000),
                new CollectedModelUsage(0, "model-without-rate", TokenClass.Output, 300),
            ]);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey, Rates());

        Assert.NotNull(summary);
        Assert.Equal(CostBasis.Published, summary.PublishedCost.Basis);
        Assert.Equal(CostStatus.NoRate, summary.PublishedCost.Status);
        Assert.Null(summary.PublishedCost.Value);
        Problem problem = Assert.Single(summary.Problems, static p => p.Code == "reported-usage-not-priced");
        Assert.Contains("model-without-rate", problem.Message, StringComparison.Ordinal);

        SessionListFigure listFigure = Assert.Single(reader.SessionListFigures(DroidHarness, Rates()));
        Assert.Equal(CostStatus.NoRate, listFigure.PublishedCost.Status);
        Assert.Null(listFigure.PublishedCost.Value);
        Assert.True(listFigure.ProblemsCount >= 1);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void ModelWhoseRateDependsOnPromptSize_IsNotMeasured(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [
                new CollectedModelUsage(0, "gpt-6-luna", TokenClass.Input, 1000),
                new CollectedModelUsage(0, "gpt-6-luna", TokenClass.Output, 300),
            ]);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey, Rates());

        Assert.NotNull(summary);
        Assert.Equal(CostStatus.NoRate, summary.PublishedCost.Status);
        Assert.Null(summary.PublishedCost.Value);
        Assert.Contains(summary.Problems, static p => p.Code == "reported-usage-not-priced");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void TotalOnlyFigure_IsPricedOnlyWithASingleAllClassRate(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [
                new CollectedModelUsage(0, "flat-model", TokenClass.Total, 2000),
            ],
            WarpMapper.WarpHarnessId);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey, Rates());

        Assert.NotNull(summary);
        AssertPriced(summary.PublishedCost, 0.004, ("flat-model", 0.004));
        Assert.Equal(2000L, summary.ReportedTotalTokens);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void TotalOnlyFigure_WithClassSpecificRates_IsNotMeasured(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [
                new CollectedModelUsage(0, ModelA, TokenClass.Total, 2000),
            ],
            WarpMapper.WarpHarnessId);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey, Rates());

        Assert.NotNull(summary);
        Assert.Equal(CostStatus.NoRate, summary.PublishedCost.Status);
        Assert.Null(summary.PublishedCost.Value);
        Assert.Contains(summary.Problems, static p => p.Code == "reported-usage-not-priced");
        Assert.Equal(2000L, summary.ReportedTotalTokens);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void RecordsWithCounters_KeepPerTurnPricingAndIgnoreReportedUsage(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [new CollectedModelUsage(0, ModelA, TokenClass.Input, 1000)],
            DroidHarness,
            [new CollectedTokenCount(TokenClass.Input, 10), new CollectedTokenCount(TokenClass.Output, 5)]);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey, Rates());

        Assert.NotNull(summary);
        Assert.Equal(CostStatus.OutOfScope, summary.PublishedCost.Status);
        Assert.Null(summary.PublishedCost.Value);
        Assert.DoesNotContain(summary.Problems, static p => p.Code == "reported-usage-not-priced");
    }

    private static void AssertPriced(ReadTimeCostSummary cost, double expected, params (string Model, double Value)[] byModel)
    {
        Assert.Equal(CostBasis.Published, cost.Basis);
        Assert.Equal(CostStatus.Priced, cost.Status);
        Assert.NotNull(cost.Value);
        Assert.Equal(expected, cost.Value.Value, precision: 9);
        Assert.Equal("USD", cost.Currency);
        Assert.Equal(byModel.Length, cost.ByModel.Count);
        foreach ((string model, double value) in byModel)
        {
            Assert.Equal(value, Assert.Single(cost.ByModel, m => m.Model == model).Value, precision: 9);
        }
    }

    private static RateLookup Rates() =>
        new(new Dictionary<string, ModelCosts>(StringComparer.Ordinal)
        {
            [ModelA] = new ModelCosts(2e-6, 8e-6, 3e-6, 0.2e-6, 0, 1.0, CacheWriteCostIsExplicit: true),
            [ModelB] = new ModelCosts(1e-6, 4e-6, 1.25e-6, 0.1e-6, 0, 1.0, CacheWriteCostIsExplicit: true),
            ["flat-model"] = new ModelCosts(2e-6, 2e-6, 2e-6, 2e-6, 0, 1.0, CacheWriteCostIsExplicit: true),
            ["gpt-6-luna"] = new ModelCosts(1e-6, 4e-6, 1.25e-6, 0.1e-6, 0, 1.0, CacheWriteCostIsExplicit: true),
        });

    private IRelationalStoreReader Seed(
        string storeKind,
        IReadOnlyList<CollectedModelUsage> reportedUsage,
        string harnessId = DroidHarness,
        IReadOnlyList<CollectedTokenCount>? recordTokens = null)
    {
        CollectedBatch batch = new(
            new CollectedSource(harnessId, SourceKind.Otlp, "usage-cost"),
            [new CollectedSession(SessionKey, harnessId, "native-usage-cost", null, null, reportedUsage)],
            [Call(harnessId, recordTokens ?? [])],
            []);

        if (string.Equals(storeKind, "memory", StringComparison.Ordinal))
        {
            InMemoryRelationalStore memory = new();
            memory.WriteBatch(batch);
            return memory;
        }

        SqliteRelationalStore sqlite = new(Path.Combine(directory.Path, "canon.db"), new SqliteCircuitBreaker());
        sqlite.WriteBatch(batch);
        return sqlite;
    }

    private static CollectedRecord Call(string harnessId, IReadOnlyList<CollectedTokenCount> tokens) =>
        new(
            RecordId: "call-1",
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "llm_call",
            StartedAt: T0,
            Status: RecordStatus.Ok,
            SessionKey: SessionKey,
            ParentRecordId: null,
            TraceId: "trace-usage-cost",
            Model: ModelA,
            EndedAt: T0.AddSeconds(1),
            ReportedDurationMs: 1000,
            Tokens: tokens,
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);

    private class RateLookup(IReadOnlyDictionary<string, ModelCosts> rates) : IModelPricingLookup
    {
        public ModelCosts? GetModelCosts(string model) => rates.GetValueOrDefault(model);
    }
}
