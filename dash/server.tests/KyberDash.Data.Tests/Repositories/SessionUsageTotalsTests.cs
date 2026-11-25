using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Session token totals fall back to the session's reported usage (<c>session_model_usage</c>)
/// only when no record in the session carries its own counters, and are not measured
/// (<see langword="null"/>) when neither exists. Both the SQLite store and the in-memory store
/// answer the same figures.
/// </summary>
public class SessionUsageTotalsTests : IDisposable
{
    private const string HarnessId = "droid";
    private const string SessionKey = "sess-usage";
    private const string Model = "claude-sonnet";

    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory directory = new();

    /// <summary>Both stores the dashboard reads totals from.</summary>
    public static TheoryData<string> Stores => new() { "sqlite", "memory" };

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void RecordsWithoutCounters_ShowReportedSessionUsage(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [
                new CollectedModelUsage(0, Model, TokenClass.Input, 100),
                new CollectedModelUsage(0, Model, TokenClass.CacheRead, 30),
                new CollectedModelUsage(0, Model, TokenClass.CacheWrite, 20),
                new CollectedModelUsage(0, Model, TokenClass.Output, 40),
            ],
            [Call("call-1", 0, []), Call("call-2", 1, [])]);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey);

        Assert.NotNull(summary);
        Assert.Equal(150L, summary.TotalInput);
        Assert.Equal(40L, summary.TotalOutput);
        Assert.Equal(30L, summary.TotalCacheRead);
        Assert.Equal(20L, summary.TotalCacheCreation);

        SessionListFigure listFigure = Assert.Single(reader.SessionListFigures(HarnessId));
        Assert.Equal(150L, listFigure.TotalInput);
        Assert.Equal(40L, listFigure.TotalOutput);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void ReportedUsage_UsesTheLatestSnapshotAcrossModels(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [
                new CollectedModelUsage(0, Model, TokenClass.Input, 10),
                new CollectedModelUsage(0, Model, TokenClass.Output, 1),
                new CollectedModelUsage(1, Model, TokenClass.Input, 70),
                new CollectedModelUsage(1, "other-model", TokenClass.Input, 5),
                new CollectedModelUsage(1, Model, TokenClass.Output, 9),
            ],
            [Call("call-1", 0, [])]);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey);

        Assert.NotNull(summary);
        Assert.Equal(75L, summary.TotalInput);
        Assert.Equal(9L, summary.TotalOutput);
        Assert.Null(summary.TotalCacheRead);
        Assert.Null(summary.TotalCacheCreation);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void AnyRecordWithCounters_UsesRecordSumsAndIgnoresReportedUsage(string storeKind)
    {
        IRelationalStoreReader reader = Seed(
            storeKind,
            [
                new CollectedModelUsage(0, Model, TokenClass.Input, 999),
                new CollectedModelUsage(0, Model, TokenClass.Output, 999),
            ],
            [
                Call("call-1", 0, [new CollectedTokenCount(TokenClass.Input, 10), new CollectedTokenCount(TokenClass.Output, 5)]),
                Call("call-2", 1, []),
            ]);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey);

        Assert.NotNull(summary);
        Assert.Equal(10L, summary.TotalInput);
        Assert.Equal(5L, summary.TotalOutput);
        Assert.Null(summary.TotalCacheRead);
        Assert.Null(summary.TotalCacheCreation);

        SessionListFigure listFigure = Assert.Single(reader.SessionListFigures(HarnessId));
        Assert.Equal(10L, listFigure.TotalInput);
        Assert.Equal(5L, listFigure.TotalOutput);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void NoCountersAndNoReportedUsage_IsNotMeasured(string storeKind)
    {
        IRelationalStoreReader reader = Seed(storeKind, [], [Call("call-1", 0, [])]);

        SessionSummaryFigure? summary = reader.SessionSummaryFigures(SessionKey);

        Assert.NotNull(summary);
        Assert.Null(summary.TotalInput);
        Assert.Null(summary.TotalOutput);
        Assert.Null(summary.TotalCacheRead);
        Assert.Null(summary.TotalCacheCreation);

        SessionListFigure listFigure = Assert.Single(reader.SessionListFigures(HarnessId));
        Assert.Null(listFigure.TotalInput);
        Assert.Null(listFigure.TotalOutput);
    }

    private IRelationalStoreReader Seed(
        string storeKind,
        IReadOnlyList<CollectedModelUsage> reportedUsage,
        IReadOnlyList<CollectedRecord> records)
    {
        CollectedBatch batch = new(
            new CollectedSource(HarnessId, SourceKind.Otlp, "usage-totals"),
            [new CollectedSession(SessionKey, HarnessId, "native-usage", null, null, reportedUsage)],
            records,
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

    private static CollectedRecord Call(string recordId, int offsetSeconds, IReadOnlyList<CollectedTokenCount> tokens) =>
        new(
            RecordId: recordId,
            HarnessId: HarnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "llm_call",
            StartedAt: T0.AddSeconds(offsetSeconds),
            Status: RecordStatus.Ok,
            SessionKey: SessionKey,
            ParentRecordId: null,
            TraceId: "trace-usage",
            Model: Model,
            EndedAt: T0.AddSeconds(offsetSeconds + 1),
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
}
