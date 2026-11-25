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
/// units of the harnesses it names, and <see cref="JobSchedulerService.RunMaintenancePass"/>
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

        RefreshReport report = harness.Ingestion.RereadCheckpointedSources(["droid", "cursor", "warp"]);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(string.Empty, report.RunId);
        Assert.Equal(0, report.HistoryWeeks);
        Assert.Equal(["droid", "cursor", "warp"], report.Rows.Select(static row => row.HarnessId));
        Assert.All(report.Rows, static row => Assert.Equal("committed", row.Status));
        Assert.Equal(3, harness.Writer.WriteBatchCalls.Count);

        Assert.Equal("2", harness.Checkpoint("droid", harness.DroidPath("sess-a")).ParserContractVersion);
        Assert.Equal("2", harness.Checkpoint("cursor", harness.CursorPath("sess-c")).ParserContractVersion);
        Assert.Equal("2", harness.Checkpoint("warp", harness.WarpPath()).ParserContractVersion);
        Assert.Null(harness.Store.GetSourceCheckpoint("droid", harness.DroidPath("sess-uncheckpointed")));
        Assert.Equal("1", harness.Checkpoint("pi", harness.PiPath("sess-p")).ParserContractVersion);
        Assert.Empty(harness.Store.ListRefreshRuns());
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
        Assert.Equal(3, harness.Writer.WriteBatchCalls.Count);
        Assert.Equal(["droid", "cursor", "warp"], harness.Writer.WriteBatchCalls.Select(static batch => batch.Source.HarnessId));
        Assert.Equal("2", harness.Checkpoint("droid", harness.DroidPath("sess-a")).ParserContractVersion);
        Assert.Equal("2", harness.Checkpoint("cursor", harness.CursorPath("sess-c")).ParserContractVersion);
        Assert.Equal("2", harness.Checkpoint("warp", harness.WarpPath()).ParserContractVersion);
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

    /// <summary>Builds a harness with one checkpointed unit for each of Droid, Cursor and Warp.</summary>
    private static Harness CheckpointedHarness()
    {
        Harness harness = new();
        harness.SeedCheckpoint("droid", harness.DroidPath("sess-a"), "factory", "droid-reader", "droid-jsonl", ".factory/sessions");
        harness.SeedCheckpoint("cursor", harness.CursorPath("sess-c"), "cursor", "cursor-agent-transcript-reader", "cursor-agent-jsonl", ".cursor/projects");
        harness.SeedCheckpoint("warp", harness.WarpPath(), "warp", "warp-sqlite-reader", "warp-sqlite", "warp.sqlite");
        harness.AddDroidTranscript("sess-a");
        harness.AddCursorTranscript("sess-c");
        harness.AddWarpFile();
        return harness;
    }

    /// <summary>The fakes and real services one test runs over, built once per test.</summary>
    private sealed class Harness
    {
        public Harness()
        {
            FileSystem = new FakeHarnessFileSystem();
            Writer = new SpyRelationalStoreWriter(Store);
            Ingestion = new TranscriptIngestionService(
                Writer,
                Store,
                new HarnessSourceRegistry(),
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
            FileSystem.AddFile(DroidPath(sessionId), DroidTranscript(sessionId), BaselineUtc.AddMinutes(-10));

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
            string rootLabel)
        {
            string timestamp = FormatUtc(BaselineUtc.AddMinutes(-10));
            Store.UpsertSourceCheckpoint(new SourceCheckpoint(
                HarnessId: harnessId,
                SourceKey: sourceKey,
                ProviderId: providerId,
                ParserId: parserId,
                ParserContractVersion: "1",
                Format: format,
                SourceRootLabel: rootLabel,
                RevisionToken: "rev-estimated",
                CoveredFromUtc: timestamp,
                CoveredThroughUtc: timestamp,
                LastAttemptUtc: timestamp,
                LastSuccessUtc: timestamp,
                LastStatus: "ok",
                LastErrorCode: null,
                UnitCount: 1,
                RecordCount: 3));
        }

        public SourceCheckpoint Checkpoint(string harnessId, string sourceKey) =>
            Assert.IsType<SourceCheckpoint>(Store.GetSourceCheckpoint(harnessId, sourceKey));
    }

    private static string DroidTranscript(string sessionId) => string.Join('\n',
    [
        $"{{\"type\":\"session_start\",\"id\":\"{sessionId}\",\"title\":\"Estimated tokens\",\"cwd\":\"/home/dev/projects/kyber-weave\"}}",
        "{\"type\":\"message\",\"timestamp\":\"2026-11-24T11:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Inspect the repo\"}]}}",
        "{\"type\":\"message\",\"id\":\"a-1\",\"timestamp\":\"2026-11-24T11:00:02.500Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"First turn done.\"}]}}",
    ]) + "\n";

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed class SpyRelationalStoreWriter(IRelationalStoreWriter inner) : IRelationalStoreWriter
    {
        private bool failNextWriteBatch;

        public List<CollectedBatch> WriteBatchCalls { get; } = [];

        public List<CollectedProblem> WriteProblemCalls { get; } = [];

        public void FailNextWriteBatch() => failNextWriteBatch = true;

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

        public void WriteProblem(CollectedProblem problem)
        {
            WriteProblemCalls.Add(problem);
            inner.WriteProblem(problem);
        }
    }

    private sealed class FakeHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, (string Content, long LengthBytes, DateTimeOffset LastWriteUtc)> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);

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
            if (files.TryGetValue(Path.GetFullPath(filePath), out (string Content, long LengthBytes, DateTimeOffset LastWriteUtc) entry))
            {
                lengthBytes = entry.LengthBytes;
                lastWriteUtc = entry.LastWriteUtc;
                return true;
            }

            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath) => files[Path.GetFullPath(filePath)].Content;

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
