using System.Collections.Generic;
using System.Threading.Tasks;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// Verifies the scheduler's plugin-captured counter pass and the folder-import switch over fakes:
/// <see cref="JobSchedulerService.TriggerRefreshAsync"/> and
/// <see cref="JobSchedulerService.RunMaintenancePass"/> each run the counter pass once before
/// <see cref="ICanonProjectionService.ProjectAll"/>, the maintenance pass imports folder sources
/// only when <c>FolderImportScheduled</c> is set, and a paused host skips both file reads while
/// still purging and projecting.
/// </summary>
public class PluginSessionFileSchedulerTests
{
    private const string PluginSourceName = "kyberdash-factory-plugin";

    /// <summary>A manual refresh runs the counter pass once, before the projection pass.</summary>
    [Fact]
    public async Task TriggerRefresh_RunsTheCounterPassOnce_BeforeProjectAll()
    {
        SchedulerHarness harness = new();
        harness.SeedPluginCapture("sess-plug");
        harness.ClearStoreReadLog();

        JobOutcome outcome = await harness.Jobs.TriggerRefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", outcome.Outcome);
        harness.CounterPassVerify(Times.Once(), ["sess-plug"]);
        Assert.Equal(["counters", "project"], harness.Events);
        Assert.Contains(nameof(InMemoryRelationalStore.SessionKeysWithRecordSource), harness.StoreReadLog);
    }

    /// <summary>The maintenance pass runs the counter pass once, before the projection pass.</summary>
    [Fact]
    public void MaintenancePass_RunsTheCounterPassOnce_BeforeProjectAll()
    {
        SchedulerHarness harness = new();
        harness.SeedPluginCapture("sess-plug");
        harness.ClearStoreReadLog();

        JobOutcome outcome = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", outcome.Outcome);
        harness.CounterPassVerify(Times.Once(), ["sess-plug"]);
        Assert.Equal(["counters", "project"], harness.Events);
        Assert.Contains(nameof(InMemoryRelationalStore.SessionKeysWithRecordSource), harness.StoreReadLog);
    }

    /// <summary>The maintenance pass imports folder sources only when <c>FolderImportScheduled</c> is set.</summary>
    [Fact]
    public void MaintenancePass_ImportsFolderSources_OnlyWhenScheduled()
    {
        SchedulerHarness unscheduled = new();
        Assert.Equal("succeeded", unscheduled.Jobs.RunMaintenancePass().Outcome);
        unscheduled.Ingestion.Verify(
            service => service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks),
            Times.Never);

        SchedulerHarness scheduled = new();
        scheduled.Settings.WriteBooleanSetting(SettingsService.FolderImportScheduledKey, true);

        Assert.Equal("succeeded", scheduled.Jobs.RunMaintenancePass().Outcome);
        scheduled.Ingestion.Verify(
            service => service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks),
            Times.Once);
    }

    /// <summary>A paused maintenance pass reads no files but still purges and projects.</summary>
    [Fact]
    public void MaintenancePass_WhenPaused_SkipsBothFileReads_ButStillPurgesAndProjects()
    {
        SchedulerHarness harness = new();
        harness.SeedPluginCapture("sess-plug");
        harness.Settings.WriteBooleanSetting(SettingsService.JobsPausedKey, true);
        harness.Settings.WriteBooleanSetting(SettingsService.FolderImportScheduledKey, true);

        JobOutcome outcome = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", outcome.Outcome);
        harness.Ingestion.Verify(
            service => service.RefreshHarnessSources(It.IsAny<int>()),
            Times.Never);
        harness.CounterPassVerify(Times.Never(), null);
        harness.Ingestion.Verify(service => service.PurgeExpiredContent(It.IsAny<DateTimeOffset>()), Times.Once);
        harness.Projection.Verify(projectionService => projectionService.ProjectAll(), Times.Once);
    }

    /// <summary>A refresh declined while paused calls neither file read.</summary>
    [Fact]
    public async Task Refresh_WhenPaused_Declines_AndCallsNeitherRead()
    {
        SchedulerHarness harness = new();
        harness.SeedPluginCapture("sess-plug");
        harness.Settings.WriteBooleanSetting(SettingsService.JobsPausedKey, true);

        JobOutcome outcome = await harness.Jobs.TriggerRefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("declined", outcome.Outcome);
        harness.Ingestion.Verify(
            service => service.RefreshHarnessSources(It.IsAny<int>()),
            Times.Never);
        harness.CounterPassVerify(Times.Never(), null);
    }

    /// <summary>The fakes and mocks one scheduler test runs over, built once per test.</summary>
    private sealed class SchedulerHarness
    {
        public SchedulerHarness()
        {
            Store = new InMemoryRelationalStore();
            Settings = new FakeSettingsRepository();
            ServerState = new FakeServerStateRepository();
            Projection = new Mock<ICanonProjectionService>();
            Ingestion = new Mock<ITranscriptIngestionService>();
            Ingestion
                .Setup(service => service.RefreshHarnessSources(It.IsAny<int>()))
                .Returns(new RefreshReport(
                    RunId: string.Empty,
                    HistoryWeeks: 0,
                    CommandStartedAt: string.Empty,
                    Rows: [],
                    PurgedContentCount: 0,
                    Sessions: 0,
                    Runs: 0,
                    Executions: 0,
                    Rollups: 0,
                    ExitCode: 0));
            Events = [];
            IReadOnlyCollection<string>? capturedKeys = null;
            Projection
                .Setup(projectionService => projectionService.ProjectAll())
                .Callback(() => Events.Add("project"));
            Ingestion
                .Setup(service => service.RefreshPluginSessionFiles("droid", It.IsAny<IReadOnlyCollection<string>>()))
                .Callback((string harnessId, IReadOnlyCollection<string> sessionKeys) =>
                {
                    _ = harnessId;
                    capturedKeys = sessionKeys;
                    Events.Add("counters");
                });
            CapturedKeys = () => capturedKeys;
            Jobs = new JobSchedulerService(
                Store,
                Store,
                Projection.Object,
                Ingestion.Object,
                Settings,
                ServerState,
                new DashOptions { StateDirectory = ServerState.DefaultStateDirectory });
        }

        public InMemoryRelationalStore Store { get; }

        public FakeSettingsRepository Settings { get; }

        public FakeServerStateRepository ServerState { get; }

        public Mock<ICanonProjectionService> Projection { get; }

        public Mock<ITranscriptIngestionService> Ingestion { get; }

        public JobSchedulerService Jobs { get; }

        public List<string> Events { get; }

        private Func<IReadOnlyCollection<string>?> CapturedKeys { get; }

        public void SeedPluginCapture(string sessionKey) =>
            Store.WriteBatch(new CollectedBatch(
                Source: new CollectedSource("droid", SourceKind.Otlp, PluginSourceName),
                Sessions: [new CollectedSession(sessionKey, "droid", sessionKey, Workspace: null, Title: null, ReportedUsage: [])],
                Records: [PluginTurn(sessionKey)],
                Problems: []));

        public void ClearStoreReadLog() => Store.ClearReadLog();

        public IReadOnlyList<string> StoreReadLog => Store.ReadLog;

        /// <summary>Verifies the counter pass ran the expected number of times with the expected candidate keys.</summary>
        /// <param name="times">How often <c>RefreshPluginSessionFiles</c> must have run.</param>
        /// <param name="expectedKeys">The candidate keys of the one expected call, or <see langword="null"/> to skip that check.</param>
        public void CounterPassVerify(Times times, IReadOnlyCollection<string>? expectedKeys)
        {
            Ingestion.Verify(
                service => service.RefreshPluginSessionFiles("droid", It.IsAny<IReadOnlyCollection<string>>()),
                times);
            if (expectedKeys is not null)
            {
                Assert.Equal(expectedKeys, CapturedKeys());
            }
        }

        private static CollectedRecord PluginTurn(string sessionKey) => new(
            RecordId: $"plugin-{sessionKey}",
            HarnessId: "droid",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Internal,
            Name: "factory.turn",
            StartedAt: new DateTimeOffset(2026, 11, 19, 11, 59, 0, TimeSpan.Zero),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null));
    }
}
