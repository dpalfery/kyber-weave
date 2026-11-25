using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Cursor;
using KyberDash.Data.Harnesses.Droid;
using KyberDash.Data.Harnesses.Warp;
using KyberDash.Domain;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// Verifies the one-time re-read that replaces rows stored with estimated tokens:
/// <see cref="ITranscriptIngestionService.RereadCheckpointedSources"/> reads only checkpointed
/// units of the harnesses it names and clears the estimates of a checkpointed unit whose file is
/// gone, and <see cref="JobSchedulerService.RunMaintenancePass"/>
/// performs it once, records the completion marker only after a successful re-read, never while
/// jobs are paused, and retries on the next tick when the re-read fails.
/// </summary>
public class NoEstimateRereadTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "no-estimate-reread-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The re-read reads exactly the checkpointed units of the harnesses it is given.</summary>
    [Fact]
    public void Reread_ReadsOnlyCheckpointedUnitsOfTheNamedHarnesses()
    {
        Harness harness = CheckpointedHarness();
        harness.SeedCheckpoint("pi", harness.PiPath("sess-p"), "pi", "pi-reader", "pi-jsonl", ".pi/agent/sessions");
        harness.AddDroidTranscript("sess-uncheckpointed");
        harness.AddPiTranscript("sess-p");
        harness.Writer.Clear();

        RefreshReport report = harness.Ingestion.RereadCheckpointedSources(["droid", "cursor"]);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(string.Empty, report.RunId);
        Assert.Equal(0, report.HistoryWeeks);
        Assert.Equal(["droid", "cursor"], report.Rows.Select(static row => row.HarnessId));
        Assert.All(report.Rows, static row => Assert.Equal("committed", row.Status));
        Assert.Equal(2, harness.Writer.WriteBatchCalls.Count);

        Assert.Equal("2", harness.Checkpoint("droid", harness.DroidPath("sess-a")).ParserContractVersion);
        Assert.Equal("2", harness.Checkpoint("cursor", harness.CursorPath("sess-c")).ParserContractVersion);
        Assert.Null(harness.Store.GetSourceCheckpoint("droid", harness.DroidPath("sess-uncheckpointed")));
        Assert.Equal("1", harness.Checkpoint("pi", harness.PiPath("sess-p")).ParserContractVersion);
        Assert.Empty(harness.Store.ListRefreshRuns());
    }

    /// <summary>
    /// A re-read that replaces rows moves the store's write marker, so the projection pass does not
    /// short-circuit on the re-read sessions; a re-read that writes nothing leaves it alone.
    /// </summary>
    [Fact]
    public void Reread_MovesTheWriteMarker_OnlyWhenItWritesRows()
    {
        Harness written = CheckpointedHarness();
        long writtenBefore = written.Store.GetWriteMarker();

        RefreshReport report = written.Ingestion.RereadCheckpointedSources(["droid"]);

        Assert.Equal("committed", Assert.Single(report.Rows).Status);
        Assert.True(written.Store.GetWriteMarker() > writtenBefore);

        Harness idle = new();
        long idleBefore = idle.Store.GetWriteMarker();

        RefreshReport idleReport = idle.Ingestion.RereadCheckpointedSources(["droid"]);

        Assert.Empty(idleReport.Rows);
        Assert.Equal(idleBefore, idle.Store.GetWriteMarker());
    }

    /// <summary>The first unpaused maintenance pass re-reads the checkpointed units and records the marker.</summary>
    [Fact]
    public void MaintenancePass_RereadsCheckpointedUnitsOnce_AndRecordsTheMarker()
    {
        Harness harness = CheckpointedHarness();
        harness.Writer.Clear();

        JobOutcome outcome = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", outcome.Outcome);
        Assert.NotNull(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Equal(2, harness.Writer.WriteBatchCalls.Count);
        Assert.Equal(["droid", "cursor"], harness.Writer.WriteBatchCalls.Select(static batch => batch.Source.HarnessId));
        Assert.Equal("2", harness.Checkpoint("droid", harness.DroidPath("sess-a")).ParserContractVersion);
        Assert.Equal("2", harness.Checkpoint("cursor", harness.CursorPath("sess-c")).ParserContractVersion);
    }

    /// <summary>Once the marker is recorded, the next maintenance pass re-reads nothing.</summary>
    [Fact]
    public void MaintenancePass_AfterTheMarkerIsRecorded_RereadsNone()
    {
        Harness harness = CheckpointedHarness();
        Assert.Equal("succeeded", harness.Jobs.RunMaintenancePass().Outcome);
        harness.Writer.Clear();

        JobOutcome outcome = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", outcome.Outcome);
        Assert.Empty(harness.Writer.WriteBatchCalls);
        Assert.NotNull(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Equal("2", harness.Checkpoint("droid", harness.DroidPath("sess-a")).ParserContractVersion);
    }

    /// <summary>While jobs are paused nothing is re-read and the marker stays absent.</summary>
    [Fact]
    public void MaintenancePass_WhenPaused_RereadsNothing_AndLeavesTheMarkerAbsent()
    {
        Harness harness = CheckpointedHarness();
        harness.Settings.WriteBooleanSetting(SettingsService.JobsPausedKey, true);
        harness.Writer.Clear();

        JobOutcome outcome = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", outcome.Outcome);
        Assert.Empty(harness.Writer.WriteBatchCalls);
        Assert.Null(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Equal("1", harness.Checkpoint("droid", harness.DroidPath("sess-a")).ParserContractVersion);
    }

    /// <summary>A failed re-read reports the pass partial and leaves the marker absent, so the next tick retries and records it.</summary>
    [Fact]
    public void MaintenancePass_WhenTheRereadFails_LeavesTheMarkerAbsent_AndRetriesOnTheNextTick()
    {
        Harness harness = new();
        harness.SeedCheckpoint("droid", harness.DroidPath("sess-a"), "factory", "droid-reader", "droid-jsonl", ".factory/sessions");
        harness.AddDroidTranscript("sess-a");
        harness.Writer.FailNextWriteBatch();
        harness.Writer.Clear();

        JobOutcome failed = harness.Jobs.RunMaintenancePass();

        Assert.Equal("partial", failed.Outcome);
        Assert.Equal(1, failed.FailedUnits);
        Assert.Null(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Empty(harness.Writer.WriteBatchCalls);
        Assert.Single(harness.Writer.WriteProblemCalls);
        Assert.Equal("failed", harness.Checkpoint("droid", harness.DroidPath("sess-a")).LastStatus);

        harness.Writer.Clear();
        JobOutcome retried = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", retried.Outcome);
        Assert.NotNull(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Single(harness.Writer.WriteBatchCalls);
        Assert.Equal("ok", harness.Checkpoint("droid", harness.DroidPath("sess-a")).LastStatus);
    }

    /// <summary>
    /// A checkpointed unit last written before the history window is still re-read: the one-time
    /// marker is permanent, so a unit the pass skipped would keep its estimated rows forever.
    /// </summary>
    [Fact]
    public void MaintenancePass_RereadsACheckpointedUnitOlderThanTheHistoryWindow_BeforeRecordingTheMarker()
    {
        Harness harness = new();
        harness.SeedCheckpoint("droid", harness.DroidPath("sess-old"), "factory", "droid-reader", "droid-jsonl", ".factory/sessions");
        harness.AddDroidTranscript("sess-old", BaselineUtc.AddDays(-60));
        harness.Writer.Clear();

        JobOutcome outcome = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", outcome.Outcome);
        Assert.Single(harness.Writer.WriteBatchCalls);
        Assert.Equal("2", harness.Checkpoint("droid", harness.DroidPath("sess-old")).ParserContractVersion);
        Assert.NotNull(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
    }

    /// <summary>
    /// A checkpointed file the reader lists but can neither stat nor read is reported as a failed
    /// unit, so the marker stays absent instead of recording a pass that never replaced that unit.
    /// </summary>
    [Fact]
    public void MaintenancePass_WhenACheckpointedFileIsUnreadable_LeavesTheMarkerAbsent()
    {
        Harness harness = new();
        harness.SeedCheckpoint("droid", harness.DroidPath("sess-nostat"), "factory", "droid-reader", "droid-jsonl", ".factory/sessions");
        harness.AddDroidTranscript("sess-nostat", BaselineUtc.AddDays(-60));
        harness.FileSystem.MakeUnreadable(harness.DroidPath("sess-nostat"));
        harness.Writer.Clear();

        JobOutcome outcome = harness.Jobs.RunMaintenancePass();

        Assert.Equal("partial", outcome.Outcome);
        Assert.Equal(1, outcome.FailedUnits);
        Assert.Null(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Empty(harness.Writer.WriteBatchCalls);
    }

    /// <summary>
    /// A checkpointed unit whose file is gone cannot be re-read, so the pass clears the token values
    /// its old parser stored as estimates to not measured, keeping the sessions, records, text and
    /// harness-reported counts, before it records the marker.
    /// </summary>
    [Fact]
    public void MaintenancePass_ClearsTheEstimatedTokens_OfCheckpointedUnitsWhoseFileIsGone_BeforeRecordingTheMarker()
    {
        Harness harness = new();
        harness.SeedGoneVersionOneUnits();
        long markerBefore = harness.Store.GetWriteMarker();
        harness.Writer.Clear();

        JobOutcome outcome = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", outcome.Outcome);
        Assert.NotNull(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Empty(harness.Writer.WriteBatchCalls);

        // Droid split the session total evenly across calls: every record count was an estimate.
        // The session's own reported usage is what Droid reported, so it stays.
        Assert.Empty(harness.Store.TokenCounts("droid-gone-call"));
        Assert.Single(harness.Store.SessionModelUsages("droid-gone"));

        // Cursor estimated from text length only when it reported no counts: the bubble with
        // reported counts keeps them, the turns it estimated lose theirs.
        Assert.Equal(2, harness.Store.TokenCounts("cursor-native").Count);
        Assert.Equal(2, harness.Store.ReportedTotals("cursor-native").Count);
        Assert.Empty(harness.Store.TokenCounts("cursor-estimated"));

        // Warp allocated the conversation total across exchanges and stored the share as both the
        // count and the reported input, so both are estimates.
        Assert.Empty(harness.Store.TokenCounts("warp-exchange"));
        Assert.Empty(harness.Store.ReportedTotals("warp-exchange"));

        // A harness the pass does not name keeps everything.
        Assert.Single(harness.Store.TokenCounts("pi-call"));

        // History stays: sessions, records and their text.
        Assert.Equal(5, harness.Store.CountRecords());
        Assert.Equal(5, harness.Store.Sessions().Count);
        Assert.Single(harness.Store.FindRecordParts("droid-gone-call"));
        Assert.Single(harness.Store.FindRecordParts("cursor-estimated"));
        Assert.Single(harness.Store.FindRecordParts("warp-exchange"));

        // The checkpoints keep the old version, so a file that comes back is still re-read in full.
        Assert.Equal("1", harness.Checkpoint("droid", harness.DroidPath("sess-gone")).ParserContractVersion);
        Assert.True(harness.Store.GetWriteMarker() > markerBefore);
    }

    /// <summary>
    /// A unit whose re-read failed carries the new version on its failed checkpoint while its stored
    /// rows are still the old estimates; once its file is gone, those estimates are cleared too.
    /// </summary>
    [Fact]
    public void Reread_ClearsTheEstimatedTokens_OfAFailedUnitWhoseFileIsGone()
    {
        Harness harness = new();
        harness.SeedGoneDroidUnit(parserContractVersion: "2", lastStatus: "failed");
        harness.Writer.Clear();

        RefreshReport report = harness.Ingestion.RereadCheckpointedSources(["droid"]);

        Assert.Equal(0, report.ExitCode);
        RefreshSourceStatusRow row = Assert.Single(report.Rows);
        Assert.Equal("cleared", row.Status);
        Assert.Empty(harness.Store.TokenCounts("droid-gone-call"));
        Assert.Single(harness.Store.SessionModelUsages("droid-gone"));
    }

    /// <summary>A clear that fails is a failed unit, so the marker stays absent and the next tick retries it.</summary>
    [Fact]
    public void MaintenancePass_WhenClearingAGoneUnitFails_LeavesTheMarkerAbsent_AndRetriesOnTheNextTick()
    {
        Harness harness = new();
        harness.SeedGoneDroidUnit(parserContractVersion: "1", lastStatus: "ok");
        harness.Writer.FailNextClear();

        JobOutcome failed = harness.Jobs.RunMaintenancePass();

        Assert.Equal("partial", failed.Outcome);
        Assert.Equal(1, failed.FailedUnits);
        Assert.Null(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Equal(2, harness.Store.TokenCounts("droid-gone-call").Count);

        JobOutcome retried = harness.Jobs.RunMaintenancePass();

        Assert.Equal("succeeded", retried.Outcome);
        Assert.NotNull(harness.Store.GetRefreshRun(JobSchedulerService.NoEstimateRereadKey));
        Assert.Empty(harness.Store.TokenCounts("droid-gone-call"));
    }

    /// <summary>
    /// One pass re-reads a checkpointed unit that discovery still returns and clears the estimates
    /// of a checkpointed unit of the same harness that discovery no longer returns.
    /// </summary>
    [Fact]
    public void Reread_CommitsARediscoveredUnit_AndClearsAVanishedUnit_OfTheSameHarness()
    {
        Harness harness = new();
        harness.SeedCheckpoint("droid", harness.DroidPath("sess-a"), "factory", "droid-reader", "droid-jsonl", ".factory/sessions");
        harness.AddDroidTranscript("sess-a");
        harness.SeedGoneDroidUnit(parserContractVersion: "1", lastStatus: "ok");
        harness.Writer.Clear();

        RefreshReport report = harness.Ingestion.RereadCheckpointedSources(["droid"]);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(
            [(harness.DroidPath("sess-a"), "committed"), (harness.DroidPath("sess-gone"), "cleared")],
            report.Rows.Select(static row => (row.SourceKey, row.Status)));
        Assert.Empty(harness.Store.TokenCounts("droid-gone-call"));
        Assert.Single(harness.Store.FindRecordParts("droid-gone-call"));
    }

    /// <summary>
    /// A version 1 Warp unit was keyed by its database file. The database still exists, but discovery
    /// now returns one unit per conversation (<c>&lt;database&gt;#&lt;conversation id&gt;</c>), so the
    /// file-level key is never rediscovered: its old estimates are cleared, and the conversation unit,
    /// which the store has no checkpoint for, is left for the next folder import rather than re-read.
    /// </summary>
    [Fact]
    public void Reread_ClearsTheEstimatedTokens_OfAFileLevelWarpUnitNowKeyedPerConversation()
    {
        Harness harness = new(new SingleWarpConversationSqliteSource());
        harness.AddWarpFile();
        harness.SeedStoredUnit("warp", harness.WarpPath(), "warp-legacy", StoredRecord("warp-legacy-exchange", "warp", "warp-legacy", [new(TokenClass.Input, 12)], reportedInput: 12));
        harness.Writer.Clear();

        RefreshReport report = harness.Ingestion.RereadCheckpointedSources(["warp"]);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(
            [(harness.WarpPath(), "cleared")],
            report.Rows.Select(static row => (row.SourceKey, row.Status)));
        Assert.Empty(harness.Writer.WriteBatchCalls);
        Assert.Empty(harness.Store.TokenCounts("warp-legacy-exchange"));
        Assert.Empty(harness.Store.ReportedTotals("warp-legacy-exchange"));
        Assert.Single(harness.Store.FindRecordParts("warp-legacy-exchange"));
        Assert.Null(harness.Store.GetSourceCheckpoint("warp", $"{harness.WarpPath()}#{SingleWarpConversationSqliteSource.ConversationId}"));
        Assert.Equal("1", harness.Checkpoint("warp", harness.WarpPath()).ParserContractVersion);
    }

    /// <summary>A named harness with no registered mapper still has the estimates of its checkpointed units cleared.</summary>
    [Fact]
    public void Reread_ClearsTheEstimatedTokens_OfANamedHarnessWithNoMapper()
    {
        Harness harness = new();
        harness.SeedGoneDroidUnit(parserContractVersion: "1", lastStatus: "ok");
        TranscriptIngestionService withoutDroidMapper = new(
            harness.Writer,
            harness.Store,
            new HarnessSourceRegistry(),
            [new CursorMapper(), new WarpMapper()],
            harness.FileSystem,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport report = withoutDroidMapper.RereadCheckpointedSources(["droid"]);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal("cleared", Assert.Single(report.Rows).Status);
        Assert.Empty(harness.Store.TokenCounts("droid-gone-call"));
    }

    /// <summary>Builds a harness with one checkpointed unit for each of Droid and Cursor.</summary>
    private static Harness CheckpointedHarness()
    {
        Harness harness = new();
        harness.SeedCheckpoint("droid", harness.DroidPath("sess-a"), "factory", "droid-reader", "droid-jsonl", ".factory/sessions");
        harness.SeedCheckpoint("cursor", harness.CursorPath("sess-c"), "cursor", "cursor-agent-transcript-reader", "cursor-agent-jsonl", ".cursor/projects");
        harness.AddDroidTranscript("sess-a");
        harness.AddCursorTranscript("sess-c");
        return harness;
    }

    /// <summary>The fakes and real services one test runs over, built once per test.</summary>
    private sealed class Harness
    {
        public Harness(IHarnessSqliteSource? sqlite = null)
        {
            FileSystem = new FakeHarnessFileSystem();
            Writer = new SpyRelationalStoreWriter(Store);
            Ingestion = new TranscriptIngestionService(
                Writer,
                Store,
                new HarnessSourceRegistry(sqlite),
                [new DroidSessionMapper(), new CursorMapper(), new WarpMapper()],
                FileSystem,
                new DashOptions { StateDirectory = StateRoot },
                homeDirectory: HomeRoot,
                clock: () => BaselineUtc);
            Jobs = new JobSchedulerService(
                Store,
                Store,
                Projection.Object,
                Ingestion,
                Settings,
                ServerState,
                new DashOptions { StateDirectory = ServerState.DefaultStateDirectory },
                clock: () => BaselineUtc);
        }

        public InMemoryRelationalStore Store { get; } = new();

        public SpyRelationalStoreWriter Writer { get; }

        public FakeSettingsRepository Settings { get; } = new();

        public FakeServerStateRepository ServerState { get; } = new();

        public Mock<ICanonProjectionService> Projection { get; } = new();

        public FakeHarnessFileSystem FileSystem { get; }

        public TranscriptIngestionService Ingestion { get; }

        public JobSchedulerService Jobs { get; }

        public string DroidPath(string sessionId) =>
            Combine(HomeRoot, $".factory/sessions/repo/{sessionId}.jsonl");

        public string CursorPath(string sessionId) =>
            Combine(HomeRoot, $".cursor/projects/proj/agent-transcripts/{sessionId}.jsonl");

        public string WarpPath() =>
            Combine(HomeRoot, "Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Stable/warp.sqlite");

        public string PiPath(string sessionId) =>
            Combine(HomeRoot, $".pi/agent/sessions/{sessionId}.jsonl");

        public void AddDroidTranscript(string sessionId) =>
            AddDroidTranscript(sessionId, BaselineUtc.AddMinutes(-10));

        public void AddDroidTranscript(string sessionId, DateTimeOffset lastWriteUtc) =>
            FileSystem.AddFile(DroidPath(sessionId), DroidTranscript(sessionId), lastWriteUtc);

        public void AddCursorTranscript(string sessionId) =>
            FileSystem.AddFile(CursorPath(sessionId), "{\"role\":\"user\",\"message\":\"hi\"}\n", BaselineUtc.AddMinutes(-10));

        public void AddWarpFile() =>
            FileSystem.AddFile(WarpPath(), "SQLite format 3\0", BaselineUtc.AddMinutes(-10));

        public void AddPiTranscript(string sessionId) =>
            FileSystem.AddFile(PiPath(sessionId), "{\"type\":\"session\"}\n", BaselineUtc.AddMinutes(-10));

        public void SeedCheckpoint(
            string harnessId,
            string sourceKey,
            string providerId,
            string parserId,
            string format,
            string rootLabel,
            string parserContractVersion = "1",
            string lastStatus = "ok")
        {
            string timestamp = FormatUtc(BaselineUtc.AddMinutes(-10));
            Store.UpsertSourceCheckpoint(new SourceCheckpoint(
                HarnessId: harnessId,
                SourceKey: sourceKey,
                ProviderId: providerId,
                ParserId: parserId,
                ParserContractVersion: parserContractVersion,
                Format: format,
                SourceRootLabel: rootLabel,
                RevisionToken: "rev-estimated",
                CoveredFromUtc: timestamp,
                CoveredThroughUtc: timestamp,
                LastAttemptUtc: timestamp,
                LastSuccessUtc: timestamp,
                LastStatus: lastStatus,
                LastErrorCode: null,
                UnitCount: 1,
                RecordCount: 3));
        }

        /// <summary>
        /// Stores a Droid unit as the version 1 reader wrote it, with the session total split across
        /// the call's counts, and checkpoints it without a file on disk.
        /// </summary>
        public void SeedGoneDroidUnit(string parserContractVersion, string lastStatus)
        {
            string sourceKey = DroidPath("sess-gone");
            SeedCheckpoint("droid", sourceKey, "factory", "droid-reader", "droid-jsonl", ".factory/sessions", parserContractVersion, lastStatus);
            WriteStoredUnit(
                "droid",
                sourceKey,
                "droid-gone",
                [new CollectedModelUsage(0, "claude-sonnet", TokenClass.Input, 900)],
                StoredRecord("droid-gone-call", "droid", "droid-gone", [new(TokenClass.Input, 450), new(TokenClass.Output, 80)]));
        }

        /// <summary>
        /// Stores one gone unit for each of Droid, Cursor and Warp as their version 1 readers wrote
        /// them, plus a gone Pi unit, all checkpointed at version 1 with no file on disk.
        /// </summary>
        public void SeedGoneVersionOneUnits()
        {
            SeedGoneDroidUnit(parserContractVersion: "1", lastStatus: "ok");

            string cursorKey = CursorPath("sess-gone");
            SeedCheckpoint("cursor", cursorKey, "cursor", "cursor-agent-transcript-reader", "cursor-agent-jsonl", ".cursor/projects");
            WriteStoredUnit(
                "cursor",
                cursorKey,
                "cursor-gone",
                [],
                StoredRecord("cursor-native", "cursor", "cursor-gone", [new(TokenClass.Input, 30), new(TokenClass.Output, 5)], reportedInput: 30, reportedOutput: 5),
                StoredRecord("cursor-estimated", "cursor", "cursor-gone", [new(TokenClass.Input, 4), new(TokenClass.Output, 8), new(TokenClass.Reasoning, 2)]));

            string warpKey = Combine(HomeRoot, "gone/warp.sqlite");
            SeedCheckpoint("warp", warpKey, "warp", "warp-sqlite-reader", "warp-sqlite", "warp.sqlite");
            WriteStoredUnit(
                "warp",
                warpKey,
                "warp-gone",
                [],
                StoredRecord("warp-exchange", "warp", "warp-gone", [new(TokenClass.Input, 12)], reportedInput: 12));

            string piKey = PiPath("sess-gone");
            SeedCheckpoint("pi", piKey, "pi", "pi-reader", "pi-jsonl", ".pi/agent/sessions");
            WriteStoredUnit(
                "pi",
                piKey,
                "pi-gone",
                [],
                StoredRecord("pi-call", "pi", "pi-gone", [new(TokenClass.Input, 7)]));

            // A session the gone units do not own, so the count shows no session was removed.
            Store.WriteBatch(new CollectedBatch(
                new CollectedSource("pi", SourceKind.File, PiPath("sess-other")),
                [new CollectedSession("pi-other", "pi", "pi-other", Workspace: null, Title: null, ReportedUsage: [])],
                [],
                []));
        }

        /// <summary>Checkpoints <paramref name="sourceKey"/> at version 1 and stores one record for it as the old reader wrote it.</summary>
        public void SeedStoredUnit(string harnessId, string sourceKey, string sessionKey, CollectedRecord record)
        {
            SeedCheckpoint(harnessId, sourceKey, harnessId, $"{harnessId}-reader", $"{harnessId}-format", harnessId);
            WriteStoredUnit(harnessId, sourceKey, sessionKey, [], record);
        }

        private void WriteStoredUnit(
            string harnessId,
            string sourceKey,
            string sessionKey,
            IReadOnlyList<CollectedModelUsage> reportedUsage,
            params CollectedRecord[] records) =>
            Store.WriteBatch(new CollectedBatch(
                new CollectedSource(harnessId, SourceKind.File, sourceKey),
                [new CollectedSession(sessionKey, harnessId, sessionKey, Workspace: null, Title: null, ReportedUsage: reportedUsage)],
                records,
                []));

        public SourceCheckpoint Checkpoint(string harnessId, string sourceKey) =>
            Assert.IsType<SourceCheckpoint>(Store.GetSourceCheckpoint(harnessId, sourceKey));
    }

    private static string DroidTranscript(string sessionId) => string.Join('\n',
    [
        $"{{\"type\":\"session_start\",\"id\":\"{sessionId}\",\"title\":\"Estimated tokens\",\"cwd\":\"/home/dev/projects/kyber-weave\"}}",
        "{\"type\":\"message\",\"timestamp\":\"2026-11-24T11:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Inspect the repo\"}]}}",
        "{\"type\":\"message\",\"id\":\"a-1\",\"timestamp\":\"2026-11-24T11:00:02.500Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"First turn done.\"}]}}",
    ]) + "\n";

    private static CollectedRecord StoredRecord(
        string spanId,
        string harnessId,
        string sessionKey,
        IReadOnlyList<CollectedTokenCount> tokens,
        long? reportedInput = null,
        long? reportedOutput = null) => new(
        RecordId: spanId,
        HarnessId: harnessId,
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Internal,
        Name: "llm.invoke",
        StartedAt: BaselineUtc.AddDays(-30),
        Status: RecordStatus.Ok,
        SessionKey: sessionKey,
        ParentRecordId: null,
        TraceId: null,
        Model: "claude-sonnet",
        EndedAt: null,
        ReportedDurationMs: null,
        Tokens: tokens,
        ReportedCost: null,
        Parts: [new CollectedContentPart(0, ContentBucket.UserMessage, "Inspect the repo", null, null, null, "user", null, null)],
        Tool: null,
        Agent: null,
        SkillsInvoked: [],
        AgentInstanceId: null,
        ToolDefinitions: [],
        Signals: new CollectedRecordSignals(null, null, null, null),
        ReportedInputTokens: reportedInput,
        ReportedOutputTokens: reportedOutput);

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed class SpyRelationalStoreWriter(IRelationalStoreWriter inner) : IRelationalStoreWriter
    {
        private bool failNextWriteBatch;
        private bool failNextClear;

        public List<CollectedBatch> WriteBatchCalls { get; } = [];

        public List<CollectedProblem> WriteProblemCalls { get; } = [];

        public void FailNextWriteBatch() => failNextWriteBatch = true;

        public void FailNextClear() => failNextClear = true;

        public void Clear()
        {
            WriteBatchCalls.Clear();
            WriteProblemCalls.Clear();
        }

        public void WriteBatch(CollectedBatch batch)
        {
            if (failNextWriteBatch)
            {
                failNextWriteBatch = false;
                throw new IOException("simulated source write failure");
            }

            WriteBatchCalls.Add(batch);
            inner.WriteBatch(batch);
        }

        public void DeleteSource(string sourceId) => inner.DeleteSource(sourceId);

        public void ClearEstimatedRecordTokens(string harnessId, string sourceLocator, EstimatedRecordTokens estimated)
        {
            if (failNextClear)
            {
                failNextClear = false;
                throw new IOException("simulated clear failure");
            }

            inner.ClearEstimatedRecordTokens(harnessId, sourceLocator, estimated);
        }

        public void WriteProblem(CollectedProblem problem)
        {
            WriteProblemCalls.Add(problem);
            inner.WriteProblem(problem);
        }
    }

    /// <summary>
    /// A Warp database with the current schema and one conversation, enough for discovery to list
    /// that conversation as a unit; any other path fails to open.
    /// </summary>
    private sealed class SingleWarpConversationSqliteSource : IHarnessSqliteSource
    {
        public const string ConversationId = "conv-1";

        public IHarnessSqliteDatabase Open(string databasePath) =>
            databasePath.EndsWith("warp.sqlite", StringComparison.Ordinal)
                ? new Database()
                : throw new IOException($"no fake database at '{databasePath}'");

        private sealed class Database : IHarnessSqliteDatabase
        {
            private static readonly WarpSqliteConversationSource Schema = new();

            public IReadOnlySet<string> GetColumns(string table) =>
                Schema.RequiredColumns.TryGetValue(table, out IReadOnlyList<string>? columns)
                    ? new HashSet<string>(columns, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);

            public IEnumerable<IReadOnlyDictionary<string, object?>> Query(HarnessSqliteQuery query) =>
                string.Equals(query.Name, "warp.conversations", StringComparison.Ordinal)
                    ? [new Dictionary<string, object?> { ["conversation_id"] = ConversationId, ["last_modified_at"] = "2026-11-24 11:50:00" }]
                    : throw new IOException($"unexpected query '{query.Name}'");

            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, (string Content, long LengthBytes, DateTimeOffset LastWriteUtc)> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);
        private readonly HashSet<string> unreadable = new(StringComparer.Ordinal);

        /// <summary>
        /// Makes <see cref="TryGetFileStats"/> and <see cref="ReadAllText"/> fail for <paramref name="path"/>
        /// while the file stays listed, as for a file removed or locked after it was enumerated.
        /// </summary>
        public void MakeUnreadable(string path) => _ = unreadable.Add(Path.GetFullPath(path));

        public void AddFile(string path, string content, DateTimeOffset lastWriteUtc)
        {
            string normalized = Path.GetFullPath(path);
            files[normalized] = (content, Encoding.UTF8.GetByteCount(content), lastWriteUtc);

            string? dir = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrEmpty(dir))
            {
                _ = directories.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        public bool DirectoryExists(string path) =>
            directories.Contains(Path.GetFullPath(path));

        public bool FileExists(string path) =>
            files.ContainsKey(Path.GetFullPath(path));

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive)
        {
            string normalizedRoot = Path.GetFullPath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            List<string> matches = [];
            foreach (string filePath in files.Keys.OrderBy(static key => key, StringComparer.Ordinal))
            {
                string? parent = Path.GetDirectoryName(filePath);
                if (parent is null)
                {
                    continue;
                }

                bool inScope = recursive
                    ? string.Equals(parent, normalizedRoot, StringComparison.Ordinal)
                      || parent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    : string.Equals(parent, normalizedRoot, StringComparison.Ordinal);

                if (inScope && MatchesPattern(Path.GetFileName(filePath), searchPattern))
                {
                    matches.Add(filePath);
                }
            }

            return matches;
        }

        public IEnumerable<string> EnumerateDirectories(string directoryPath)
        {
            string normalizedRoot = Path.GetFullPath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            return directories
                .Where(directory => string.Equals(Path.GetDirectoryName(directory), normalizedRoot, StringComparison.Ordinal))
                .OrderBy(static directory => directory, StringComparer.Ordinal)
                .ToList();
        }

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            if (!unreadable.Contains(Path.GetFullPath(filePath)) && files.TryGetValue(Path.GetFullPath(filePath), out (string Content, long LengthBytes, DateTimeOffset LastWriteUtc) entry))
            {
                lengthBytes = entry.LengthBytes;
                lastWriteUtc = entry.LastWriteUtc;
                return true;
            }

            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath) =>
            unreadable.Contains(Path.GetFullPath(filePath))
                ? throw new IOException("simulated unreadable file")
                : files[Path.GetFullPath(filePath)].Content;

        public string GetFullPath(string path) => Path.GetFullPath(path);

        private static bool MatchesPattern(string fileName, string pattern)
        {
            if (string.Equals(pattern, "*", StringComparison.Ordinal))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
