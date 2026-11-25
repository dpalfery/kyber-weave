using System.Reflection;
using System.Runtime.ExceptionServices;
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

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public void ARecordWrittenDuringAMaintenancePass_IsRebuiltByTheNextPass(string kind)
    {
        StoreUnderTest store = Create(kind);
        CanonSessionProjectorEngine engine = NewEngine(store, store.Reader);

        WriteSession(store.Writer, "sess-a", [Turn("turn-1", "sess-a", minuteOffset: 0)]);
        Assert.Equal(1, engine.ProjectIncremental().Built);
        WriteSession(store.Writer, "sess-a", [Turn("turn-2", "sess-a", minuteOffset: 1)]);

        long passStartMarker = store.Operational.GetWriteMarker();
        IRelationalStoreReader midPassReader = MidPassWriteReader.Create(
            store.Reader,
            "sess-a",
            () => WriteSession(store.Writer, "sess-a", [Turn("turn-3", "sess-a", minuteOffset: 2)]));
        ProjectionPassReport midPass = NewEngine(store, midPassReader).ProjectIncremental();

        Assert.Equal(["sess-a"], midPass.RebuiltKeys);
        Assert.Equal(passStartMarker, store.Operational.GetProjectionState()?.RecordsMarker);
        Assert.False(engine.IsProjectionCurrent());

        ProjectionPassReport next = NewService(store).ProjectIncremental();

        Assert.Contains("sess-a", next.RebuiltKeys);
        Assert.Equal(3, store.Projections.GetSessionProjection("sess-a")?.SpanCount);
        Assert.True(engine.IsProjectionCurrent());
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public void ARecordWrittenDuringAReadPassThatDefers_StaysDirtyWithTheDeferredKey(string kind)
    {
        StoreUnderTest store = Create(kind);
        CanonSessionProjectorEngine engine = NewEngine(store, store.Reader);

        WriteSession(store.Writer, "sess-small", [Turn("small-1", "sess-small", minuteOffset: 0)]);
        WriteSession(
            store.Writer,
            "sess-big",
            [
                Turn("big-1", "sess-big", minuteOffset: 0, harnessId: "codex"),
                Turn("big-2", "sess-big", minuteOffset: 1, harnessId: "codex"),
            ],
            harnessId: "codex");
        Assert.Equal(2, engine.ProjectIncremental().Built);
        WriteSession(store.Writer, "sess-small", [Turn("small-2", "sess-small", minuteOffset: 1)]);
        WriteSession(store.Writer, "sess-big", [Turn("big-3", "sess-big", minuteOffset: 2, harnessId: "codex")], harnessId: "codex");

        long passStartMarker = store.Operational.GetWriteMarker();
        IRelationalStoreReader midPassReader = MidPassWriteReader.Create(
            store.Reader,
            "sess-small",
            () => WriteSession(store.Writer, "sess-small", [Turn("small-3", "sess-small", minuteOffset: 2)]));
        CanonSessionProjectorEngine readEngine = NewEngine(store, midPassReader, readPathSessionRecordBudget: 2);
        ProjectionPassReport readPass = readEngine.ProjectIncrementalForReads();

        Assert.Equal(["sess-small"], readPass.RebuiltKeys);
        Assert.Equal(["sess-big"], readPass.DeferredKeys);
        Assert.Equal(passStartMarker, store.Operational.GetProjectionState()?.RecordsMarker);
        DirtyProjectionState? dirty = store.Operational.GetProjectionDirtyState();
        Assert.NotNull(dirty);
        Assert.Equal(passStartMarker, dirty!.FromSeq);
        Assert.Equal(store.Operational.GetWriteMarker(), dirty.ToSeq);
        Assert.Equal(["sess-big", "sess-small"], dirty.Keys!.Order(StringComparer.Ordinal));

        ProjectionPassReport next = NewService(store).ProjectIncremental();

        Assert.Contains("sess-small", next.RebuiltKeys);
        Assert.Contains("sess-big", next.RebuiltKeys);
        Assert.Equal(3, store.Projections.GetSessionProjection("sess-small")?.SpanCount);
        Assert.Null(store.Operational.GetProjectionDirtyState());
    }

    private static CanonSessionProjectorEngine NewEngine(
        StoreUnderTest store,
        IRelationalStoreReader reader,
        int readPathSessionRecordBudget = CanonSessionProjectorEngine.DefaultReadPathSessionRecordBudget) =>
        new(reader, store.Projections, store.Operational, new FakePricingRepository(), readPathSessionRecordBudget);

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

    /// <summary>
    /// Reader decorator that writes one batch through the real store writer right after the
    /// projection pass reads a given session's records, so the write lands after that read but
    /// before the pass stamps its watermark: the mid-pass window. The write moves the marker
    /// itself, as every record-changing write does.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="DispatchProxy"/> so every other reader member delegates to the store
    /// under test without a hand-written forwarder per member; <see cref="DispatchProxy"/>
    /// requires an unsealed proxy type with a public parameterless constructor.
    /// </remarks>
#pragma warning disable CA1852 // DispatchProxy derives a generated type from this one.
    public class MidPassWriteReader : DispatchProxy
#pragma warning restore CA1852
    {
        private IRelationalStoreReader inner = null!;
        private string sessionKey = string.Empty;
        private Action? write;

        /// <summary>Wraps <paramref name="inner"/> so <paramref name="write"/> runs once after <paramref name="sessionKey"/>'s records are read.</summary>
        /// <param name="inner">The store under test.</param>
        /// <param name="sessionKey">Session whose record read triggers the write.</param>
        /// <param name="write">The mid-pass write.</param>
        /// <returns>The decorated reader.</returns>
        public static IRelationalStoreReader Create(IRelationalStoreReader inner, string sessionKey, Action write)
        {
            IRelationalStoreReader proxy = Create<IRelationalStoreReader, MidPassWriteReader>();
            MidPassWriteReader self = (MidPassWriteReader)proxy;
            self.inner = inner;
            self.sessionKey = sessionKey;
            self.write = write;
            return proxy;
        }

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            object? result;
            try
            {
                result = targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }

            if (write is not null
                && string.Equals(targetMethod.Name, nameof(IRelationalStoreReader.SessionRecordsForRebuild), StringComparison.Ordinal)
                && args is [string key]
                && string.Equals(key, sessionKey, StringComparison.Ordinal))
            {
                Action pending = write;
                write = null;
                pending();
            }

            return result;
        }
    }

    private sealed record StoreUnderTest(
        IRelationalStoreWriter Writer,
        IRelationalStoreReader Reader,
        IProjectionStore Projections,
        IOperationalStore Operational);
}
