using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// Contract tests for the write-then-rebuild loop: a record-changing store write moves the
/// records write marker and marks its sessions dirty, so the next
/// <see cref="CanonProjectionService.ProjectIncremental"/> pass rebuilds exactly those sessions,
/// on both <see cref="InMemoryRelationalStore"/> and <see cref="SqliteRelationalStore"/>.
/// </summary>
public class ProjectionWriteMarkerContractTests : IDisposable
{
    private static readonly DateTimeOffset TurnBase = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public void ASecondRecordForASession_RebuildsThatSessionOnTheNextPass(string kind)
    {
        StoreUnderTest store = Create(kind);
        CanonProjectionService service = NewService(store);

        WriteSession(store.Writer, "sess-a", [Turn("turn-1", "sess-a", minuteOffset: 0)]);
        ProjectionPassReport first = service.ProjectIncremental();
        Assert.Equal(1, first.Built);
        Assert.Equal(1, store.Projections.GetSessionProjection("sess-a")?.SpanCount);

        WriteSession(store.Writer, "sess-a", [Turn("turn-2", "sess-a", minuteOffset: 1)]);
        ProjectionPassReport second = service.ProjectIncremental();

        Assert.Equal(1, second.Built);
        Assert.Equal(["sess-a"], second.RebuiltKeys);
        Assert.Equal(2, store.Projections.GetSessionProjection("sess-a")?.SpanCount);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public void ABatchForAnotherSession_RebuildsOnlyThatSession(string kind)
    {
        StoreUnderTest store = Create(kind);
        CanonProjectionService service = NewService(store);

        WriteSession(store.Writer, "sess-a", [Turn("turn-a", "sess-a", minuteOffset: 0)]);
        Assert.Equal(1, service.ProjectIncremental().Built);

        WriteSession(store.Writer, "sess-b", [Turn("turn-b", "sess-b", minuteOffset: 0, harnessId: "codex")], harnessId: "codex");
        ProjectionPassReport second = service.ProjectIncremental();

        Assert.Equal(["sess-b"], second.RebuiltKeys);
        Assert.Contains("sess-a", second.SkippedKeys);
        Assert.NotNull(store.Projections.GetSessionProjection("sess-b"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public void NoWriteBetweenPasses_ShortCircuitsTheSecondPass(string kind)
    {
        StoreUnderTest store = Create(kind);
        CanonProjectionService service = NewService(store);

        WriteSession(store.Writer, "sess-a", [Turn("turn-1", "sess-a", minuteOffset: 0)]);
        Assert.Equal(1, service.ProjectIncremental().Built);

        ProjectionPassReport second = service.ProjectIncremental();

        Assert.Equal(0, second.Built);
        Assert.Empty(second.RebuiltKeys);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public void DeletingASessionsSource_PrunesItOnTheNextPass(string kind)
    {
        StoreUnderTest store = Create(kind);
        CanonProjectionService service = NewService(store);

        WriteSession(store.Writer, "sess-a", [Turn("turn-a", "sess-a", minuteOffset: 0)]);
        WriteSession(store.Writer, "sess-b", [Turn("turn-b", "sess-b", minuteOffset: 0, harnessId: "codex")], harnessId: "codex");
        Assert.Equal(2, service.ProjectIncremental().Built);

        store.Writer.DeleteSource(store.Reader.Sources("claude-code").Single().SourceId);
        ProjectionPassReport second = service.ProjectIncremental();

        Assert.Equal(1, second.Pruned);
        Assert.Null(store.Projections.GetSessionProjection("sess-a"));
        Assert.NotNull(store.Projections.GetSessionProjection("sess-b"));
    }

    private static CanonProjectionService NewService(StoreUnderTest store)
    {
        FakePricingRepository pricing = new();
        CanonSessionProjectorEngine engine = new(store.Reader, store.Projections, store.Operational, pricing);
        return new CanonProjectionService(
            store.Reader,
            store.Projections,
            store.Operational,
            engine,
            new CanonRollupsEngine(store.Reader, store.Projections, pricing));
    }

    private StoreUnderTest Create(string kind)
    {
        if (string.Equals(kind, "sqlite", StringComparison.Ordinal))
        {
            SqliteRelationalStore sqlite = new(directory.Combine($"marker-{Guid.NewGuid():N}.db"), new SqliteCircuitBreaker());
            return new StoreUnderTest(sqlite, sqlite, sqlite, sqlite);
        }

        if (string.Equals(kind, "memory", StringComparison.Ordinal))
        {
            InMemoryRelationalStore memory = new();
            return new StoreUnderTest(memory, memory, memory, memory);
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown store kind.");
    }

    private static void WriteSession(
        IRelationalStoreWriter writer,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records,
        string harnessId = "claude-code")
    {
        writer.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions:
            [
                new CollectedSession(
                    SessionKey: sessionKey,
                    HarnessId: harnessId,
                    NativeSessionId: sessionKey,
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
            ],
            Records: records,
            Problems: []));
    }

    private static CollectedRecord Turn(
        string spanId,
        string sessionKey,
        int minuteOffset,
        string harnessId = "claude-code")
    {
        List<CollectedTokenCount> tokens =
        [
            new CollectedTokenCount(TokenClass.Input, 300),
            new CollectedTokenCount(TokenClass.CacheRead, 0),
            new CollectedTokenCount(TokenClass.CacheWrite, 0),
            new CollectedTokenCount(TokenClass.Output, 30),
        ];

        return new CollectedRecord(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: TurnBase.AddMinutes(minuteOffset),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: "claude-sonnet-4-6",
            EndedAt: null,
            ReportedDurationMs: 1_000,
            Tokens: tokens,
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(ErrorMessage: null, EditFailures: null, CorrectionRule: null, DeclaredContextWindow: null),
            ReportedInputTokens: 300,
            ReportedOutputTokens: 30);
    }

    private sealed record StoreUnderTest(
        IRelationalStoreWriter Writer,
        IRelationalStoreReader Reader,
        IProjectionStore Projections,
        IOperationalStore Operational);
}
