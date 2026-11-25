using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Application;
using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Droid;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// Verifies <see cref="TranscriptIngestionService.RefreshPluginSessionFiles"/> over the in-memory
/// filesystem and store: a plugin-captured Droid session gains its transcript's file rows, the
/// companion's token usage as session usage, and its model; candidates without a file are skipped
/// and reported nowhere; uncaptured files are not read; an empty candidate set reads nothing; the
/// checkpoint makes a second call write nothing; a later full refresh keeps one copy of the
/// counters; and a traversal-shaped candidate id never reads outside the sessions root.
/// </summary>
public class PluginSessionFileRefreshTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "plugin-session-refresh-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private const string PluginSourceName = "kyberdash-factory-plugin";

    /// <summary>A captured session with its transcript and companion on disk gains the file rows, usage, and model.</summary>
    [Fact]
    public void CapturedSession_GainsFileRows_CompanionUsage_AndModel()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        WritePluginCapture(store, "sess-droid-1");
        WriteDroidSessionFiles(fs, "sess-droid-1");
        TranscriptIngestionService service = CreateService(store, spyWriter, fs);

        RefreshReport report = service.RefreshPluginSessionFiles("droid", ["sess-droid-1"]);

        Assert.Equal(0, report.ExitCode);
        RefreshSourceStatusRow row = Assert.Single(report.Rows);
        Assert.Equal("droid", row.HarnessId);
        Assert.Equal(DroidTranscriptPath("sess-droid-1"), row.SourceKey);
        Assert.Equal("committed", row.Status);
        Assert.Equal(2, row.RecordsWritten);

        CollectedRecord? fileRecord = store.FindRecord("synth:droid:sess-droid-1:a-1");
        Assert.NotNull(fileRecord);
        Assert.Equal("droid", fileRecord.HarnessId);
        Assert.Equal("sess-droid-1", fileRecord.SessionKey);
        Assert.Equal("GLM-5.1", fileRecord.Model);
        Assert.Equal(3, store.CountRecords("droid"));

        IReadOnlyList<StoredSessionModelUsage> usages = store.SessionModelUsages("sess-droid-1");
        Assert.Equal(5, usages.Count);
        Assert.Contains(usages, static u => u.Usage.TokenClass == TokenClass.Input && u.Usage.Count == 101L);
        Assert.Contains(usages, static u => u.Usage.TokenClass == TokenClass.Output && u.Usage.Count == 41L);

        // No refresh_run row and no history coverage claimed by this pass.
        Assert.Empty(store.ListRefreshRuns());
        Assert.Equal(string.Empty, report.RunId);
        Assert.Equal(0, report.HistoryWeeks);
        Assert.Single(spyWriter.WriteBatchCalls);
    }

    /// <summary>A candidate id with no transcript file is skipped and reported nowhere as a failure.</summary>
    [Fact]
    public void CandidateIdWithNoFile_IsSkippedAndReportedNowhere()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        WritePluginCapture(store, "sess-missing");
        WriteDroidSessionFiles(fs, "sess-other");
        TranscriptIngestionService service = CreateService(store, spyWriter, fs);

        RefreshReport report = service.RefreshPluginSessionFiles("droid", ["sess-missing"]);

        Assert.Empty(report.Rows);
        Assert.Empty(store.ListIngestProblems("droid"));
        Assert.Equal(1, store.CountRecords("droid"));
        Assert.Empty(spyWriter.WriteBatchCalls);
        Assert.Null(store.GetSourceCheckpoint("droid", DroidTranscriptPath("sess-other")));
    }

    /// <summary>A Droid file whose session the plugin did not capture is not read.</summary>
    [Fact]
    public void UncapturedDroidFile_IsNotRead()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        WritePluginCapture(store, "sess-captured");
        WriteDroidSessionFiles(fs, "sess-captured");
        WriteDroidSessionFiles(fs, "sess-uncaptured");
        TranscriptIngestionService service = CreateService(store, spyWriter, fs);

        RefreshReport report = service.RefreshPluginSessionFiles("droid", ["sess-captured"]);

        RefreshSourceStatusRow row = Assert.Single(report.Rows);
        Assert.Equal(DroidTranscriptPath("sess-captured"), row.SourceKey);
        Assert.Equal(3, store.CountRecords("droid"));
        Assert.Null(store.FindRecord("synth:droid:sess-uncaptured:a-1"));
        Assert.Null(store.GetSourceCheckpoint("droid", DroidTranscriptPath("sess-uncaptured")));
        Assert.Single(spyWriter.WriteBatchCalls);
    }

    /// <summary>An empty candidate set reads nothing.</summary>
    [Fact]
    public void EmptyCandidateSet_ReadsNothing()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        WriteDroidSessionFiles(fs, "sess-droid-1");
        TranscriptIngestionService service = CreateService(store, spyWriter, fs);

        RefreshReport report = service.RefreshPluginSessionFiles("droid", []);

        Assert.Empty(report.Rows);
        Assert.Equal(0, store.CountRecords("droid"));
        Assert.Empty(spyWriter.WriteBatchCalls);
    }

    /// <summary>A second call with an unchanged file writes nothing — the checkpoint is reused.</summary>
    [Fact]
    public void SecondCallWithUnchangedFile_WritesNothing()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        WritePluginCapture(store, "sess-droid-1");
        WriteDroidSessionFiles(fs, "sess-droid-1");
        TranscriptIngestionService service = CreateService(store, spyWriter, fs);

        RefreshReport first = service.RefreshPluginSessionFiles("droid", ["sess-droid-1"]);
        Assert.Equal("committed", Assert.Single(first.Rows).Status);

        spyWriter.WriteBatchCalls.Clear();
        RefreshReport second = service.RefreshPluginSessionFiles("droid", ["sess-droid-1"]);

        Assert.Equal("skipped", Assert.Single(second.Rows).Status);
        Assert.Empty(spyWriter.WriteBatchCalls);
        Assert.Equal(3, store.CountRecords("droid"));
    }

    /// <summary>A later full refresh over the same file leaves one copy of the counters under the same source id.</summary>
    [Fact]
    public void LaterRefreshHarnessSources_LeavesOneCopyOfTheCounters()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        WritePluginCapture(store, "sess-droid-1");
        WriteDroidSessionFiles(fs, "sess-droid-1");
        TranscriptIngestionService service = CreateService(store, spyWriter, fs);

        _ = service.RefreshPluginSessionFiles("droid", ["sess-droid-1"]);
        int afterPluginPass = store.CountRecords();
        StoredSource fileSource = Assert.Single(
            store.Sources("droid"),
            static s => s.Source.Kind == SourceKind.File);

        service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks);

        Assert.Equal(afterPluginPass, store.CountRecords());
        StoredSource refreshedFileSource = Assert.Single(
            store.Sources("droid"),
            static s => s.Source.Kind == SourceKind.File);
        Assert.Equal(fileSource.SourceId, refreshedFileSource.SourceId);
        Assert.NotNull(store.FindRecord("synth:droid:sess-droid-1:a-1"));
        Assert.NotNull(store.FindRecord("synth:droid:sess-droid-1:a-2"));
    }

    /// <summary>A candidate id containing separators or dot segments never reads outside the sessions root.</summary>
    [Fact]
    public void TraversalLikeCandidateIds_NeverReadOutsideTheSessionsRoot()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        WritePluginCapture(store, "sess-droid-1");
        WriteDroidSessionFiles(fs, "sess-droid-1");

        // A same-stem transcript outside the sessions root would be reachable only by joining the
        // id into a path; stem matching over discovery results must never reach it.
        fs.AddFile(Combine(HomeRoot, "escape/sess-droid-1.jsonl"), DroidTranscript("sess-droid-1"), BaselineUtc.AddMinutes(-10));
        TranscriptIngestionService service = CreateService(store, spyWriter, fs);

        RefreshReport report = service.RefreshPluginSessionFiles("droid", ["..", "../escape", "repo/../sess-droid-1", "../../escape/sess-droid-1"]);

        Assert.Empty(report.Rows);
        Assert.Equal(1, store.CountRecords("droid"));
        Assert.Null(store.GetSourceCheckpoint("droid", Combine(HomeRoot, "escape/sess-droid-1.jsonl")));
        Assert.Null(store.GetSourceCheckpoint("droid", DroidTranscriptPath("sess-droid-1")));
        Assert.Empty(spyWriter.WriteBatchCalls);
    }

    private static TranscriptIngestionService CreateService(
        InMemoryRelationalStore store,
        SpyRelationalStoreWriter spyWriter,
        FakeHarnessFileSystem fs) =>
        new(
            spyWriter,
            store,
            new HarnessSourceRegistry(),
            [new DroidSessionMapper()],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

    private static void WritePluginCapture(InMemoryRelationalStore store, string sessionKey) =>
        store.WriteBatch(new CollectedBatch(
            Source: new CollectedSource("droid", SourceKind.Otlp, PluginSourceName),
            Sessions: [new CollectedSession(sessionKey, "droid", sessionKey, Workspace: null, Title: null, ReportedUsage: [])],
            Records: [PluginTurn(sessionKey)],
            Problems: []));

    private static CollectedRecord PluginTurn(string sessionKey) => new(
        RecordId: $"plugin-{sessionKey}",
        HarnessId: "droid",
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Internal,
        Name: "factory.turn",
        StartedAt: BaselineUtc.AddMinutes(-1),
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

    private static string DroidTranscriptPath(string sessionId) =>
        Combine(HomeRoot, $".factory/sessions/repo/{sessionId}.jsonl");

    private static void WriteDroidSessionFiles(FakeHarnessFileSystem fs, string sessionId)
    {
        fs.AddFile(DroidTranscriptPath(sessionId), DroidTranscript(sessionId), BaselineUtc.AddMinutes(-10));
        fs.AddFile(
            Combine(HomeRoot, $".factory/sessions/repo/{sessionId}.settings.json"),
            """
            {
              "model": "custom:GLM-5.1-[Proxy]-0",
              "tokenUsage": {
                "inputTokens": 101,
                "outputTokens": 41,
                "cacheCreationTokens": 10,
                "cacheReadTokens": 203,
                "thinkingTokens": 7
              }
            }
            """,
            BaselineUtc.AddMinutes(-9));
    }

    /// <summary>The transcript shape <see cref="DroidSessionMapper"/> maps: a session start and two assistant turns.</summary>
    private static string DroidTranscript(string sessionId) => string.Join('\n',
    [
        $"{{\"type\":\"session_start\",\"id\":\"{sessionId}\",\"title\":\"Plugin captured\",\"cwd\":\"/home/dev/projects/kyber-weave\"}}",
        "{\"type\":\"message\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Inspect the repo\"}]}}",
        "{\"type\":\"message\",\"id\":\"a-1\",\"timestamp\":\"2026-11-19T10:00:02.500Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"First turn done.\"}]}}",
        "{\"type\":\"message\",\"id\":\"a-2\",\"timestamp\":\"2026-11-19T10:00:14.000Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Summary ready.\"}]}}",
    ]) + "\n";

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed class SpyRelationalStoreWriter(IRelationalStoreWriter inner) : IRelationalStoreWriter
    {
        public List<CollectedBatch> WriteBatchCalls { get; } = [];

        public List<string> DeleteSourceCalls { get; } = [];

        public List<CollectedProblem> WriteProblemCalls { get; } = [];

        public void WriteBatch(CollectedBatch batch)
        {
            WriteBatchCalls.Add(batch);
            inner.WriteBatch(batch);
        }

        public void DeleteSource(string sourceId)
        {
            DeleteSourceCalls.Add(sourceId);
            inner.DeleteSource(sourceId);
        }

        public void ClearEstimatedRecordTokens(string harnessId, string sourceLocator, EstimatedRecordTokens estimated) =>
            inner.ClearEstimatedRecordTokens(harnessId, sourceLocator, estimated);

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
            foreach (string filePath in files.Keys.OrderBy(static k => k, StringComparer.Ordinal))
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
                .Where(d => string.Equals(Path.GetDirectoryName(d), normalizedRoot, StringComparison.Ordinal))
                .OrderBy(static d => d, StringComparer.Ordinal)
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
