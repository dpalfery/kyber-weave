using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// Verifies <see cref="CanonRefreshEngine.FullReingest"/> runs exclusively over
/// <see cref="IRelationalStoreWriter"/>, <see cref="IRelationalStoreReader"/>, and
/// <see cref="IOperationalStore"/>, and reports per-harness session and record counts.
/// </summary>
public class FullReingestTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "full-reingest-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies a full re-ingest over the refresh history window commits every discovered unit
    /// and returns per-harness session and record counts.
    /// </summary>
    [Fact]
    public void FullReingest_CommitsAllUnitsAndReportsPerHarnessSessionAndRecordCounts()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string commandcodePath = Combine(HomeRoot, ".commandcode/projects/repo/u1.jsonl");
        string piPath = Combine(HomeRoot, ".pi/agent/sessions/p1.jsonl");
        fs.AddFile(
            commandcodePath,
            "{\"id\":\"cc-rec-1\",\"session\":\"cc-sess-a\"}\n{\"id\":\"cc-rec-2\",\"session\":\"cc-sess-a\"}\n",
            BaselineUtc.AddMinutes(-10));
        fs.AddFile(
            piPath,
            "{\"id\":\"pi-rec-1\",\"session\":\"pi-sess-a\"}\n",
            BaselineUtc.AddMinutes(-5));

        CanonRefreshEngine engine = MakeEngine(store, spyWriter, fs, [new StubHarnessMapper("commandcode"), new StubHarnessMapper("pi")]);

        FullReingestReport report = engine.FullReingest(historyWeeks: 2);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(2, report.HistoryWeeks);
        Assert.Equal(2, report.Rows.Count);
        Assert.All(report.Rows, static row => Assert.Equal("committed", row.Status));

        HarnessReingestCounts commandcode = Assert.Single(report.Harnesses, static h => h.HarnessId == "commandcode");
        Assert.Equal(1, commandcode.Sessions);
        Assert.Equal(2, commandcode.Records);

        HarnessReingestCounts pi = Assert.Single(report.Harnesses, static h => h.HarnessId == "pi");
        Assert.Equal(1, pi.Sessions);
        Assert.Equal(1, pi.Records);

        Assert.Equal(2, store.CountRecords("commandcode"));
        Assert.Equal(1, store.CountRecords("pi"));
        Assert.Single(store.Sessions("commandcode"));
        Assert.Single(store.Sessions("pi"));

        Assert.Equal("ok", store.GetSourceCheckpoint("commandcode", commandcodePath)?.LastStatus);
        Assert.Equal("ok", store.GetSourceCheckpoint("pi", piPath)?.LastStatus);
        Assert.Single(store.ListRefreshRuns());

        string expectedCutoff = BaselineUtc.AddDays(-CanonRefreshEngine.ContentRetentionDays)
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        Assert.Equal(0, report.PurgedContentCount);
        Assert.Equal(CanonRefreshEngine.ContentRetentionDays, store.GetContentRetentionDays());
        Assert.Equal(expectedCutoff, store.GetContentPurgeMarkUtc());
    }

    /// <summary>
    /// Verifies repeating a full re-ingest replaces prior source rows instead of duplicating them,
    /// keeping per-harness counts stable.
    /// </summary>
    [Fact]
    public void FullReingest_ReplacesPriorSourceRowsWithoutDuplicatingCounts()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string commandcodePath = Combine(HomeRoot, ".commandcode/projects/repo/u1.jsonl");
        fs.AddFile(commandcodePath, "{\"id\":\"cc-rec-1\",\"session\":\"cc-sess-a\"}\n", BaselineUtc.AddMinutes(-10));

        CanonRefreshEngine engine = MakeEngine(store, spyWriter, fs, [new StubHarnessMapper("commandcode")]);

        FullReingestReport first = engine.FullReingest(historyWeeks: 2);
        FullReingestReport second = engine.FullReingest(historyWeeks: 2);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Single(spyWriter.DeleteSourceCalls);
        Assert.Equal(2, spyWriter.WriteBatchCalls.Count);
        Assert.Equal(1, store.CountRecords("commandcode"));

        HarnessReingestCounts counts = Assert.Single(second.Harnesses, static h => h.HarnessId == "commandcode");
        Assert.Equal(1, counts.Sessions);
        Assert.Equal(1, counts.Records);
    }

    /// <summary>
    /// Verifies an unreadable unit fails its row with <c>SOURCE_UNREADABLE</c> while remaining
    /// units still commit and per-harness counts cover only committed harnesses.
    /// </summary>
    [Fact]
    public void FullReingest_UnreadableUnitFailsWithoutHaltingCommittedHarnessCounts()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string goodPath = Combine(HomeRoot, ".commandcode/projects/repo/good.jsonl");
        string unreadablePath = Combine(HomeRoot, ".pi/agent/sessions/bad.jsonl");
        fs.AddFile(goodPath, "{\"id\":\"cc-rec-1\",\"session\":\"cc-sess-a\"}\n", BaselineUtc.AddMinutes(-10));
        fs.AddFile(unreadablePath, "{\"id\":\"pi-rec-1\",\"session\":\"pi-sess-a\"}\n", BaselineUtc.AddMinutes(-5));
        fs.ThrowOnRead(unreadablePath, new IOException("permission denied on file"));

        CanonRefreshEngine engine = MakeEngine(store, spyWriter, fs, [new StubHarnessMapper("commandcode"), new StubHarnessMapper("pi")]);

        FullReingestReport report = engine.FullReingest(historyWeeks: 2);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Rows, r => r.SourceKey == goodPath && r.Status == "committed" && r.RecordsWritten == 1);
        Assert.Contains(report.Rows, r => r.SourceKey == unreadablePath && r.Status == "failed");

        Assert.Equal(1, store.CountRecords("commandcode"));
        Assert.Equal(0, store.CountRecords("pi"));
        Assert.Equal("SOURCE_UNREADABLE", store.GetSourceCheckpoint("pi", unreadablePath)?.LastErrorCode);
        Assert.Equal("failed", store.GetSourceCheckpoint("pi", unreadablePath)?.LastStatus);
        Assert.Equal("ok", store.GetSourceCheckpoint("commandcode", goodPath)?.LastStatus);

        HarnessReingestCounts commandcode = Assert.Single(report.Harnesses, static h => h.HarnessId == "commandcode");
        Assert.Equal(1, commandcode.Sessions);
        Assert.Equal(1, commandcode.Records);
        Assert.DoesNotContain(report.Harnesses, static h => h.HarnessId == "pi");
    }

    /// <summary>
    /// Verifies <see cref="ServiceCollectionExtensions.AddDashApplication"/> wires
    /// <see cref="CanonRefreshEngine"/> through the relational store contracts without calling
    /// <see cref="ICanonStoreRepository"/>.
    /// </summary>
    [Fact]
    public void ServiceCollectionExtensions_WiresCanonRefreshEngineThroughRelationalContracts()
    {
        ConstructorInfo? relationalCtor = typeof(CanonRefreshEngine).GetConstructor(
        [
            typeof(IRelationalStoreWriter),
            typeof(IRelationalStoreReader),
            typeof(IOperationalStore),
            typeof(HarnessSourceRegistry),
            typeof(IReadOnlyList<IHarnessMapper>),
            typeof(IHarnessFileSystem),
            typeof(CanonSessionProjectorEngine),
            typeof(CanonRollupsEngine),
            typeof(DashOptions),
            typeof(string),
            typeof(Func<DateTimeOffset>),
        ]);
        Assert.NotNull(relationalCtor);
        Assert.DoesNotContain(relationalCtor.GetParameters(), static p => p.ParameterType == typeof(ICanonStoreRepository));

        InMemoryRelationalStore store = new();
        FakeHarnessFileSystem fs = new();
        Mock<ICanonStoreRepository> forbiddenCanonStore = new(MockBehavior.Strict);

        ServiceCollection services = new();
        services.AddSingleton<IRelationalStoreWriter>(store);
        services.AddSingleton<IRelationalStoreReader>(store);
        services.AddSingleton<IOperationalStore>(store);
        services.AddSingleton<IProjectionStore>(store);
        services.AddSingleton<IPricingRepository>(new FakePricingRepository());
        services.AddSingleton<ICanonStoreRepository>(forbiddenCanonStore.Object);
        services.AddSingleton<IHarnessFileSystem>(fs);
        services.AddSingleton<IHarnessMapper>(new StubHarnessMapper("commandcode"));

        _ = services.AddDashApplication(new DashOptions { StateDirectory = StateRoot });

        using ServiceProvider provider = services.BuildServiceProvider();
        CanonRefreshEngine engine = provider.GetRequiredService<CanonRefreshEngine>();

        FullReingestReport report = engine.FullReingest(historyWeeks: 2);

        Assert.Equal(0, report.ExitCode);
        Assert.Empty(report.Harnesses);
        forbiddenCanonStore.VerifyNoOtherCalls();
    }

    private static CanonRefreshEngine MakeEngine(
        InMemoryRelationalStore store,
        IRelationalStoreWriter writer,
        FakeHarnessFileSystem fs,
        IReadOnlyList<IHarnessMapper> mappers) =>
        new(
            writer,
            store,
            store,
            new HarnessSourceRegistry(),
            mappers,
            fs,
            null,
            null,
            new DashOptions { StateDirectory = StateRoot },
            HomeRoot,
            () => BaselineUtc);

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed class SpyRelationalStoreWriter(IRelationalStoreWriter inner) : IRelationalStoreWriter
    {
        public List<CollectedBatch> WriteBatchCalls { get; } = [];

        public List<string> DeleteSourceCalls { get; } = [];

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

        public void WriteProblem(CollectedProblem problem) => inner.WriteProblem(problem);
    }

    private sealed class StubHarnessMapper(string harnessId) : IHarnessMapper
    {
        public string HarnessId { get; } = harnessId;

        public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
        {
            ArgumentNullException.ThrowIfNull(unit);

            string rawText = unit.RawText ?? string.Empty;
            List<CollectedRecord> records = [];
            foreach (string line in rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string id = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? throw new InvalidDataException("Record id must be a string.")
                    : throw new InvalidDataException("Record is missing string id.");
                string session = root.TryGetProperty("session", out JsonElement sessionEl) && sessionEl.ValueKind == JsonValueKind.String
                    ? sessionEl.GetString() ?? "sess-default"
                    : "sess-default";

                records.Add(new CollectedRecord(
                    RecordId: id,
                    HarnessId: HarnessId,
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: string.Create(CultureInfo.InvariantCulture, $"{HarnessId}.turn"),
                    StartedAt: importedAtUtc,
                    Status: RecordStatus.Ok,
                    SessionKey: session,
                    ParentRecordId: null,
                    TraceId: null,
                    Model: "test-model",
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: [new CollectedTokenCount(TokenClass.Input, 10), new CollectedTokenCount(TokenClass.Output, 5)],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)));
            }

            List<CollectedSession> sessions = [.. records
                .Select(static r => r.SessionKey)
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .Select(key => new CollectedSession(key!, HarnessId, key!, null, null, []))];

            return new CollectedBatch(
                Source: new CollectedSource(HarnessId, SourceKind.File, unit.Unit.SourceKey),
                Sessions: sessions,
                Records: records,
                Problems: []);
        }
    }

    private sealed class FakeHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, (string Content, long LengthBytes, DateTimeOffset LastWriteUtc)> files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> readFailures = new(StringComparer.Ordinal);
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

        public void ThrowOnRead(string path, Exception exception) =>
            readFailures[Path.GetFullPath(path)] = exception;

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

        public string ReadAllText(string filePath)
        {
            string normalized = Path.GetFullPath(filePath);
            if (readFailures.TryGetValue(normalized, out Exception? exception))
            {
                throw exception;
            }

            return files[normalized].Content;
        }

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
