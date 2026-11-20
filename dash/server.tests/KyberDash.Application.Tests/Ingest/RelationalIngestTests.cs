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
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// Verifies relational ingestion routing across <see cref="TranscriptIngestionService"/>,
/// <see cref="CanonIngestPipeline"/>, <c>IngestSpanNormalizer</c>, and DI registration.
/// </summary>
public class RelationalIngestTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "relational-ingest-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies <see cref="TranscriptIngestionService.IngestSpans"/> writes records and problems
    /// exclusively through <see cref="IRelationalStoreWriter"/> and quarantined spans through
    /// <see cref="IOperationalStore"/> with zero raw payload persisted.
    /// </summary>
    [Fact]
    public void IngestSpans_WritesOnlyViaRelationalWriterAndOperationalStore_WithZeroRawPayload()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        TranscriptIngestionService service = new(spyWriter, store, new HarnessSourceRegistry());

        OtlpSpan copilotTurn = MakeOtlpSpan(
            spanId: "1111111111111111",
            traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            serviceName: "copilot-host",
            name: "copilot.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Element("sess-copilot"),
                ["gen_ai.request.model"] = Element("gpt-5"),
                ["gen_ai.system_instructions"] = Element("Be concise."),
                ["gen_ai.input.messages"] = Element("[{\"role\":\"user\",\"content\":\"hello\"}]"),
                ["gen_ai.usage.input_tokens"] = Element(1000),
                ["gen_ai.usage.cache_read_input_tokens"] = Element(600),
                ["gen_ai.usage.cache_creation_input_tokens"] = Element(100),
                ["gen_ai.usage.output_tokens"] = Element(200),
            });

        OtlpSpan piTurn = MakeOtlpSpan(
            spanId: "2222222222222222",
            traceId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            serviceName: "pi-host",
            name: "pi.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.skills"] = Element("debugging"),
                ["gen_ai.usage.input_tokens"] = Element(50),
                ["gen_ai.usage.cache_read_input_tokens"] = Element(200),
                ["gen_ai.usage.output_tokens"] = Element(30),
            });

        OtlpSpan badClaudeOutput = MakeOtlpSpan(
            spanId: "3333333333333333",
            traceId: "cccccccccccccccccccccccccccccccc",
            serviceName: "claude-host",
            name: "claude.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["claude.session_id"] = Element("sess-bad"),
                ["gen_ai.usage.input_tokens"] = Element(10),
                ["gen_ai.usage.output_tokens"] = Element(-5),
            });

        OtlpSpan unknownSpan = MakeOtlpSpan(
            spanId: "4444444444444444",
            traceId: "dddddddddddddddddddddddddddddddd",
            serviceName: "unknown-host",
            name: "mystery.event",
            kind: "internal",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["mystery.event_type"] = Element("unrecognized"),
                ["mystery.secret_payload"] = Element("do-not-persist"),
            });

        IngestOutcome outcome = service.IngestSpans([copilotTurn, piTurn, badClaudeOutput, unknownSpan]);

        Assert.Equal(2, outcome.Accepted);
        Assert.Equal(1, outcome.Quarantined);
        Assert.Equal(1, outcome.Rejected);
        Assert.NotEmpty(spyWriter.WriteBatchCalls);
        Assert.Equal(2, store.CountRecords());

        CollectedRecord? copilotRecord = store.GetRecord("1111111111111111");
        Assert.NotNull(copilotRecord);
        Assert.Equal("copilot", copilotRecord.HarnessId);
        Assert.Equal("sess-copilot", copilotRecord.SessionKey);
        Assert.Equal("gpt-5", copilotRecord.Model);
        Assert.Contains(copilotRecord.Parts, static p => p.Bucket == ContentBucket.SystemPrompt && p.PlainText == "Be concise.");
        Assert.Contains(copilotRecord.Parts, static p => p.Bucket == ContentBucket.ConversationHistory && p.PlainText is null && p.Withheld is not null);

        CollectedRecord? piRecord = store.GetRecord("2222222222222222");
        Assert.NotNull(piRecord);
        Assert.Equal("pi", piRecord.HarnessId);

        Assert.Null(store.GetRecord("3333333333333333"));
        Assert.Equal(1, store.CountIngestProblems());
        StoredIngestProblem problem = Assert.Single(store.ListIngestProblems());
        Assert.Equal(CanonValidation.TokenNegativeClass, problem.Problem.Code);
        Assert.Equal("claude-code", problem.Problem.HarnessId);
        Assert.DoesNotContain("do-not-persist", problem.Problem.Message, StringComparison.Ordinal);

        Assert.Null(store.GetRecord("4444444444444444"));
        Assert.Empty(store.ContentParts("4444444444444444"));
        Assert.Equal(1, store.CountQuarantinedSpans());
        StoredQuarantinedSpan? quarantined = store.GetQuarantinedSpan("4444444444444444");
        Assert.NotNull(quarantined);
        Assert.Equal("unclaimed", quarantined.Reason);
        Assert.Equal(["mystery"], quarantined.ObservedNamespaces);
    }

    /// <summary>
    /// Verifies <see cref="CanonIngestPipeline.IngestBatch(IReadOnlyList{OtlpSpan}, IRelationalStoreWriter, IOperationalStore, IReadOnlyList{IHarnessAdapter}?)"/>
    /// persists accepted records, problems, and quarantined spans through the relational contracts.
    /// </summary>
    [Fact]
    public void CanonIngestPipeline_IngestBatch_AcceptsRelationalWriterAndOperationalStore()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);

        OtlpSpan validSpan = MakeOtlpSpan(
            spanId: "5555555555555555",
            traceId: "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            serviceName: "pi-svc",
            name: "pi.turn",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["pi.session.id"] = Element("sess-pi-direct"),
                ["gen_ai.session.id"] = Element("sess-pi-direct"),
                ["gen_ai.usage.input_tokens"] = Element(25),
                ["gen_ai.usage.output_tokens"] = Element(10),
            });

        IngestOutcome outcome = CanonIngestPipeline.IngestBatch([validSpan], spyWriter, store);

        Assert.Equal(new IngestOutcome(1, 0, 0), outcome);
        Assert.Single(spyWriter.WriteBatchCalls);
        Assert.NotNull(store.GetRecord("5555555555555555"));
        Assert.NotNull(store.GetSession("sess-pi-direct"));
    }

    /// <summary>
    /// Verifies local source discovery and mapping persist exclusively via <see cref="IRelationalStoreWriter"/>,
    /// skip unchanged revisions via checkpoints, and replace changed sources cleanly.
    /// </summary>
    [Fact]
    public void RefreshHarnessSources_WritesMappedBatchesOnlyViaRelationalWriter_AndReusesCheckpoints()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string unitOnePath = Combine(HomeRoot, ".commandcode/projects/repo/u1.jsonl");
        string unitTwoPath = Combine(HomeRoot, ".commandcode/projects/repo/u2.jsonl");
        fs.AddFile(unitOnePath, "{\"type\":\"turn\",\"id\":\"rec-1\"}\n", BaselineUtc.AddMinutes(-10));
        fs.AddFile(unitTwoPath, "{\"type\":\"turn\",\"id\":\"rec-2\"}\n", BaselineUtc.AddMinutes(-5));

        CountingCommandCodeMapper mapper = new();
        TranscriptIngestionService service = new(
            spyWriter,
            store,
            new HarnessSourceRegistry(),
            [mapper],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport first = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(2, mapper.InvocationCount);
        Assert.Equal(2, spyWriter.WriteBatchCalls.Count);
        Assert.Equal(2, store.CountRecords("commandcode"));
        Assert.Equal(2, store.Sources("commandcode").Count);
        Assert.Equal("ok", store.GetSourceCheckpoint("commandcode", unitOnePath)?.LastStatus);
        Assert.Equal("ok", store.GetSourceCheckpoint("commandcode", unitTwoPath)?.LastStatus);
        Assert.Single(store.ListRefreshRuns());

        RefreshReport second = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, mapper.InvocationCount);
        Assert.Equal(2, spyWriter.WriteBatchCalls.Count);
        Assert.All(second.Rows, static row => Assert.Equal("skipped", row.Status));

        fs.AddFile(unitOnePath, "{\"type\":\"turn\",\"id\":\"rec-1\",\"updated\":true}\n", BaselineUtc.AddMinutes(1));
        RefreshReport third = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(0, third.ExitCode);
        Assert.Equal(3, mapper.InvocationCount);
        Assert.Single(spyWriter.DeleteSourceCalls);
        Assert.Equal(3, spyWriter.WriteBatchCalls.Count);
        Assert.Contains(third.Rows, r => r.SourceKey == unitOnePath && r.Status == "committed");
        Assert.Contains(third.Rows, r => r.SourceKey == unitTwoPath && r.Status == "skipped");
    }

    /// <summary>
    /// Verifies unreadable or malformed source units emit <c>SOURCE_UNREADABLE</c> without halting
    /// remaining units in the same refresh pass.
    /// </summary>
    [Fact]
    public void RefreshHarnessSources_UnreadableAndMalformedUnitsEmitSourceUnreadableAndContinue()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string ioFailPath = Combine(HomeRoot, ".commandcode/projects/repo/01-io-fail.jsonl");
        string parseFailPath = Combine(HomeRoot, ".commandcode/projects/repo/02-malformed.jsonl");
        string goodPath = Combine(HomeRoot, ".commandcode/projects/repo/03-good.jsonl");

        fs.AddFile(ioFailPath, "{\"type\":\"turn\"}\n", BaselineUtc.AddMinutes(-15));
        fs.ThrowOnRead(ioFailPath, new IOException("permission denied on file"));
        fs.AddFile(parseFailPath, "{not-valid-json\n", BaselineUtc.AddMinutes(-10));
        fs.AddFile(goodPath, "{\"type\":\"turn\",\"id\":\"rec-good\"}\n", BaselineUtc.AddMinutes(-5));

        CountingCommandCodeMapper mapper = new();
        TranscriptIngestionService service = new(
            spyWriter,
            store,
            new HarnessSourceRegistry(),
            [mapper],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport report = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(1, report.ExitCode);
        Assert.Equal(3, report.Rows.Count);
        Assert.Contains(report.Rows, r => r.SourceKey == ioFailPath && r.Status == "failed");
        Assert.Contains(report.Rows, r => r.SourceKey == parseFailPath && r.Status == "failed");
        Assert.Contains(report.Rows, r => r.SourceKey == goodPath && r.Status == "committed" && r.RecordsWritten == 1);

        Assert.Single(spyWriter.WriteBatchCalls);
        Assert.Equal(1, store.CountRecords("commandcode"));
        Assert.NotNull(store.GetRecord("rec-good"));

        Assert.Equal(2, spyWriter.WriteProblemCalls.Count);
        Assert.All(spyWriter.WriteProblemCalls, static p => Assert.Equal("SOURCE_UNREADABLE", p.Code));
        Assert.Equal(2, store.CountIngestProblems("commandcode"));
        Assert.Equal("SOURCE_UNREADABLE", store.GetSourceCheckpoint("commandcode", ioFailPath)?.LastErrorCode);
        Assert.Equal("failed", store.GetSourceCheckpoint("commandcode", ioFailPath)?.LastStatus);
        Assert.Equal("SOURCE_UNREADABLE", store.GetSourceCheckpoint("commandcode", parseFailPath)?.LastErrorCode);
        Assert.Equal("failed", store.GetSourceCheckpoint("commandcode", parseFailPath)?.LastStatus);
        Assert.Equal("ok", store.GetSourceCheckpoint("commandcode", goodPath)?.LastStatus);
    }

    /// <summary>
    /// Verifies unknown event types are counted in <c>ingest_problem</c> with zero payload rows
    /// or raw payloads persisted.
    /// </summary>
    [Fact]
    public void RefreshHarnessSources_UnknownEventTypesCountedWithZeroPayloadPersisted()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string unknownOnlyPath = Combine(HomeRoot, ".commandcode/projects/repo/unknown-events.jsonl");
        fs.AddFile(
            unknownOnlyPath,
            "{\"type\":\"future_unknown_event\",\"secretBody\":\"never-persist-this\"}\n{\"type\":\"another_unknown_event\",\"secretBody\":\"never-persist-either\"}\n",
            BaselineUtc.AddMinutes(-5));

        CountingCommandCodeMapper mapper = new();
        TranscriptIngestionService service = new(
            spyWriter,
            store,
            new HarnessSourceRegistry(),
            [mapper],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport report = service.RefreshHarnessSources(historyWeeks: 2);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(0, store.CountRecords());
        Assert.Equal(2, store.CountIngestProblems("commandcode"));

        IReadOnlyList<StoredIngestProblem> problems = store.ListIngestProblems("commandcode");
        Assert.Equal(2, problems.Count);
        foreach (StoredIngestProblem entry in problems)
        {
            Assert.Equal("UNKNOWN_EVENT_TYPE", entry.Problem.Code);
            Assert.DoesNotContain("never-persist", entry.Problem.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("{", entry.Problem.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Verifies <c>IngestSpanNormalizer</c> produces relational DTO batch output directly.
    /// </summary>
    [Fact]
    public void IngestSpanNormalizer_DropsPipelineSinkAndProducesRelationalDtoBatchOutput()
    {
        Type? normalizerType = typeof(TranscriptIngestionService).Assembly.GetType("KyberDash.Application.IngestSpanNormalizer");
        Assert.NotNull(normalizerType);

        Type[] nestedTypes = normalizerType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(nestedTypes, static t => string.Equals(t.Name, "PipelineSink", StringComparison.Ordinal));

        OtlpSpan validSpan = MakeOtlpSpan(
            spanId: "6666666666666666",
            traceId: "ffffffffffffffffffffffffffffffff",
            serviceName: "copilot-host",
            name: "copilot.chat",
            kind: "client",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["copilot_chat.chat_session_id"] = Element("sess-norm"),
                ["gen_ai.usage.input_tokens"] = Element(100),
                ["gen_ai.usage.output_tokens"] = Element(25),
            });

        OtlpSpan unclaimedSpan = MakeOtlpSpan(
            spanId: "7777777777777777",
            traceId: "ffffffffffffffffffffffffffffffff",
            serviceName: "other",
            name: "other.span",
            kind: "internal",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["custom.attr"] = Element("x"),
            });

        MethodInfo? normalizeMethod = normalizerType.GetMethod("Normalize", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(normalizeMethod);

        object? batch = normalizeMethod.Invoke(null, [new[] { validSpan, unclaimedSpan }]);
        Assert.NotNull(batch);

        Type batchType = batch.GetType();
        PropertyInfo? relationalBatchesProp = batchType.GetProperty("RelationalBatches");
        PropertyInfo? quarantinedSpansProp = batchType.GetProperty("QuarantinedSpans");
        PropertyInfo? relationalProblemsProp = batchType.GetProperty("RelationalProblems");

        Assert.NotNull(relationalBatchesProp);
        Assert.NotNull(quarantinedSpansProp);
        Assert.NotNull(relationalProblemsProp);

        IReadOnlyList<CollectedBatch> relationalBatches = Assert.IsAssignableFrom<IReadOnlyList<CollectedBatch>>(relationalBatchesProp.GetValue(batch));
        IReadOnlyList<StoredQuarantinedSpan> quarantinedSpans = Assert.IsAssignableFrom<IReadOnlyList<StoredQuarantinedSpan>>(quarantinedSpansProp.GetValue(batch));
        IReadOnlyList<CollectedProblem> relationalProblems = Assert.IsAssignableFrom<IReadOnlyList<CollectedProblem>>(relationalProblemsProp.GetValue(batch));

        CollectedBatch collected = Assert.Single(relationalBatches);
        Assert.Single(collected.Records);
        Assert.Equal("6666666666666666", collected.Records[0].RecordId);
        StoredQuarantinedSpan quarantined = Assert.Single(quarantinedSpans);
        Assert.Equal("7777777777777777", quarantined.SpanId);
        Assert.Empty(relationalProblems);
    }

    /// <summary>
    /// Verifies <see cref="ServiceCollectionExtensions.AddDashApplication"/> wires
    /// <see cref="TranscriptIngestionService"/> with <see cref="IRelationalStoreWriter"/>,
    /// <see cref="IOperationalStore"/>, and <see cref="HarnessSourceRegistry"/>.
    /// </summary>
    [Fact]
    public void ServiceCollectionExtensions_WiresTranscriptIngestionServiceThroughRelationalWriterAndOperationalStore()
    {
        InMemoryRelationalStore store = new();
        SpyRelationalStoreWriter spyWriter = new(store);
        FakeHarnessFileSystem fs = new();
        string path = Combine(HomeRoot, ".commandcode/projects/repo/di.jsonl");
        fs.AddFile(path, "{\"type\":\"turn\",\"id\":\"rec-di\"}\n", BaselineUtc);

        ServiceCollection services = new();
        services.AddSingleton<IRelationalStoreWriter>(spyWriter);
        services.AddSingleton<IOperationalStore>(store);
        services.AddSingleton<IHarnessFileSystem>(fs);
        services.AddSingleton<IHarnessMapper, CountingCommandCodeMapper>();

        _ = services.AddDashApplication(new DashOptions { StateDirectory = StateRoot });

        using ServiceProvider provider = services.BuildServiceProvider();
        ITranscriptIngestionService ingestion = provider.GetRequiredService<ITranscriptIngestionService>();

        IngestOutcome spanOutcome = ingestion.IngestSpans(
        [
            MakeOtlpSpan(
                spanId: "8888888888888888",
                traceId: "12121212121212121212121212121212",
                serviceName: "pi-svc",
                name: "pi.turn",
                kind: "client",
                attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["pi.session.id"] = Element("sess-di"),
                    ["gen_ai.session.id"] = Element("sess-di"),
                    ["gen_ai.usage.input_tokens"] = Element(12),
                    ["gen_ai.usage.output_tokens"] = Element(4),
                }),
        ]);

        Assert.Equal(1, spanOutcome.Accepted);
        Assert.NotNull(store.GetRecord("8888888888888888"));
    }

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    private static OtlpSpan MakeOtlpSpan(
        string spanId,
        string traceId,
        string serviceName,
        string name,
        string kind,
        IReadOnlyDictionary<string, JsonElement> attributes) =>
        new(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: null,
            Name: name,
            Kind: kind,
            StartTimeUnixNano: "1760000000000000000",
            EndTimeUnixNano: "1760000000100000000",
            Timestamp: "2026-11-19T10:00:00.000Z",
            DurationMs: 100.0,
            Status: new OtlpStatus("ok"),
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element(serviceName),
            },
            Scope: new OtlpScope("test-scope", "1.0.0"));

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

        public void WriteProblem(CollectedProblem problem)
        {
            WriteProblemCalls.Add(problem);
            inner.WriteProblem(problem);
        }
    }

    private sealed class CountingCommandCodeMapper : IHarnessMapper
    {
        public string HarnessId => "commandcode";

        public int InvocationCount { get; private set; }

        public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc)
        {
            InvocationCount++;
            string rawText = unit.RawText ?? string.Empty;
            List<CollectedRecord> records = [];
            List<CollectedProblem> problems = [];

            string[] lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                using JsonDocument document = JsonDocument.Parse(lines[i]);
                JsonElement root = document.RootElement;
                string eventType = root.TryGetProperty("type", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString() ?? "unknown"
                    : "unknown";

                if (string.Equals(eventType, "turn", StringComparison.Ordinal))
                {
                    string id = root.TryGetProperty("id", out JsonElement idEl) && idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString() ?? $"rec-{i}"
                        : $"rec-{i}";

                    records.Add(new CollectedRecord(
                        RecordId: id,
                        HarnessId: HarnessId,
                        Op: RecordOp.LlmInvoke,
                        Kind: RecordKind.Client,
                        Name: "commandcode.turn",
                        StartedAt: importedAtUtc,
                        Status: RecordStatus.Ok,
                        SessionKey: "sess-cc",
                        ParentRecordId: null,
                        TraceId: null,
                        Model: "claude-sonnet-4-5",
                        EndedAt: null,
                        ReportedDurationMs: 10,
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
                else
                {
                    problems.Add(new CollectedProblem(
                        ProblemKey: $"unknown-event:{unit.Unit.SourceKey}:{i}",
                        Severity: "warning",
                        Code: "UNKNOWN_EVENT_TYPE",
                        Message: string.Create(CultureInfo.InvariantCulture, $"Skipped unknown event type '{eventType}'."),
                        Location: unit.Unit.SourceKey,
                        HarnessId: HarnessId,
                        TimestampUtc: importedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));
                }
            }

            List<CollectedSession> sessions = records.Count > 0
                ? [new CollectedSession("sess-cc", HarnessId, "sess-cc", null, null, [])]
                : [];

            return new CollectedBatch(
                Source: new CollectedSource(HarnessId, SourceKind.File, unit.Unit.SourceKey),
                Sessions: sessions,
                Records: records,
                Problems: problems);
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
