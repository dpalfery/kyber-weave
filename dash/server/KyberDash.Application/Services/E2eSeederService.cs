using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Seeds an isolated relational store for a Playwright world through the production ingest,
/// projection, and rollup paths.
/// </summary>
/// <remarks>
/// A world is a recorded scenario: replaying it through the real pipeline means the E2E run proves the
/// pipeline, not a fixture writer. The seeder refuses to touch the operator's own
/// <c>~/.kyberdash/canon.db</c>, because an E2E run that writes to the live store is a data loss
/// incident, not a test failure.
/// </remarks>
public class E2eSeederService : IE2eSeederService
{
    /// <summary>World that replays the OpenCode collection fixture.</summary>
    public const string HarnessCollectionWorld = "harness-collection";

    /// <summary>World that replays the Factory hook fixtures.</summary>
    public const string FactoryReplayWorld = "factory-replay";

    /// <summary>Exit code reported when the target store is the operator's own store.</summary>
    public const int RefusedExitCode = 2;

    private readonly IRelationalStoreWriter writer;
    private readonly IProjectionStore projections;
    private readonly IOperationalStore operationalStore;
    private readonly ITranscriptIngestionService ingestion;
    private readonly ICanonProjectionService projection;

    /// <summary>
    /// Initializes a new <see cref="E2eSeederService"/>.
    /// </summary>
    /// <param name="writer">Relational writer persisting the rendering-only collected session rows.</param>
    /// <param name="projections">Projection store receiving the rendering-only projected rows.</param>
    /// <param name="operationalStore">Operational store backing the seeded world's lifecycle.</param>
    /// <param name="ingestion">Ingestion service accepting the replayed spans.</param>
    /// <param name="projection">Projection service rebuilding sessions and rollups.</param>
    public E2eSeederService(
        IRelationalStoreWriter writer,
        IProjectionStore projections,
        IOperationalStore operationalStore,
        ITranscriptIngestionService ingestion,
        ICanonProjectionService projection)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(projection);

        this.writer = writer;
        this.projections = projections;
        this.operationalStore = operationalStore;
        this.ingestion = ingestion;
        this.projection = projection;
    }

    /// <inheritdoc />
    public int SeedWorld(string world, string dbPath, string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        string fullDb = Path.GetFullPath(dbPath);
        string userCanon = Path.GetFullPath(CanonDbPath.Resolve(null, null, null));
        if (string.Equals(fullDb, userCanon, StringComparison.Ordinal))
        {
            return RefusedExitCode;
        }

        return world switch
        {
            HarnessCollectionWorld => SeedHarnessCollection(repoRoot),
            FactoryReplayWorld => SeedFactoryReplay(repoRoot),
            _ => RefusedExitCode,
        };
    }

    /// <summary>Replays the recorded OpenCode run and adds the rendering-only session rows.</summary>
    /// <param name="repoRoot">Repository root holding the fixtures.</param>
    /// <returns>Zero when the world was seeded.</returns>
    private int SeedHarnessCollection(string repoRoot)
    {
        string fixture = ResolveFirstExisting(
            Path.Combine(repoRoot, "dash", "e2e", "fixtures", "opencode", "otlp-v3-runA.consent-on.json"),
            Path.Combine(repoRoot, "dash", "src", "capture", "harnesses", "opencode-plugin", "fixtures", "v2", "otlp-v3-runA.consent-on.json"));

        byte[] rawOtlp = File.ReadAllBytes(fixture);
        IReadOnlyList<OtlpSpan> spans = OtlpCollector.DecodeTracesJson(rawOtlp);
        IngestOutcome outcome = ingestion.IngestSpans(spans);
        if (outcome.Accepted == 0)
        {
            throw new InvalidOperationException("harness-collection OpenCode run A ingest accepted zero spans.");
        }

        _ = projection.ProjectIncremental();

        SeedMeasuredClaudeCode();
        SeedRenderingOnlyOpencode();
        SeedZeroRecordDroid();

        _ = projection.ProjectRunsAndHarnesses();

        UpsertExplicitSeedRunsAndExecutions();
        StampSeededProjectionCurrent();
        return 0;
    }

    /// <summary>
    /// Stamps the seeded store as a completed projection pass would. The rendering-only sessions
    /// are written after the last real pass, so each write moved <c>store_write_marker</c> and left
    /// its session dirty; without this stamp the server's first read pass would rebuild those
    /// sessions and replace the explicit seeded runs with derived ones. It writes what a completed
    /// full maintenance pass writes, in the same order: the dirty state is cleared before the
    /// watermark and detector version are stamped.
    /// </summary>
    private void StampSeededProjectionCurrent()
    {
        operationalStore.ClearProjectionDirtyState();
        CanonSessionProjectorEngine.StampProjectionState(operationalStore, operationalStore.GetWriteMarker(), stampDetector: true);
    }

    /// <summary>Replays both Factory hook fixtures with content capture off, then on.</summary>
    /// <param name="repoRoot">Repository root holding the fixtures.</param>
    /// <returns>Zero when the world was seeded.</returns>
    private int SeedFactoryReplay(string repoRoot)
    {
        ReplayFactoryHooks(
            Path.Combine(repoRoot, "dash", "e2e", "fixtures", "factory", "content-off.json"),
            contentAllowed: false,
            baseNano: 1_793_400_000_000_000_000L);
        ReplayFactoryHooks(
            Path.Combine(repoRoot, "dash", "e2e", "fixtures", "factory", "content-on.json"),
            contentAllowed: true,
            baseNano: 1_793_400_600_000_000_000L);

        _ = projection.ProjectIncremental();
        _ = projection.ProjectRunsAndHarnesses();
        return 0;
    }

    private void ReplayFactoryHooks(string fixturePath, bool contentAllowed, long baseNano)
    {
        string json = File.ReadAllText(fixturePath, Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Factory fixture {fixturePath} must be a JSON array.");
        }

        List<OtlpSpan> spans = [];
        int index = 0;
        Dictionary<string, JsonElement> resource = new(StringComparer.Ordinal)
        {
            ["service.name"] = JsonSerializer.SerializeToElement("kyberdash-factory-plugin"),
        };
        OtlpScope scope = new("kyberdash.factory-plugin", "1.0.0");

        foreach (JsonElement hookPayload in document.RootElement.EnumerateArray())
        {
            IReadOnlyList<FactoryWireSpan> wired = FactoryHookMapper.MapHookEvent(
                hookPayload,
                new FactoryMapOptions(ContentAllowed: contentAllowed));

            foreach (FactoryWireSpan wireSpan in wired)
            {
                long startNano = baseNano + (index * 1_000_000_000L);
                long endNano = startNano + 50_000_000L;
                index++;
                DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startNano / 1_000_000L);

                Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal);
                foreach (FactoryWireEntry entry in wireSpan.Attributes)
                {
                    if (entry.Value.StringValue is not null)
                    {
                        attributes[entry.Key] = JsonSerializer.SerializeToElement(entry.Value.StringValue);
                    }
                    else if (entry.Value.IntValue is not null
                        && long.TryParse(entry.Value.IntValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integerValue))
                    {
                        attributes[entry.Key] = JsonSerializer.SerializeToElement(integerValue);
                    }
                    else if (entry.Value.DoubleValue.HasValue)
                    {
                        attributes[entry.Key] = JsonSerializer.SerializeToElement(entry.Value.DoubleValue.Value);
                    }
                }

                spans.Add(new OtlpSpan(
                    TraceId: wireSpan.TraceId,
                    SpanId: wireSpan.SpanId,
                    ParentSpanId: null,
                    Name: wireSpan.Name,
                    Kind: "internal",
                    StartTimeUnixNano: startNano.ToString(CultureInfo.InvariantCulture),
                    EndTimeUnixNano: endNano.ToString(CultureInfo.InvariantCulture),
                    Timestamp: timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    DurationMs: 50.0,
                    Status: new OtlpStatus("ok"),
                    Attributes: attributes,
                    Resource: resource,
                    Scope: scope));
            }
        }

        IngestOutcome outcome = ingestion.IngestSpans(spans);
        if (outcome.Accepted == 0)
        {
            throw new InvalidOperationException($"Factory replay ingest for {fixturePath} accepted zero spans.");
        }
    }

    private void SeedMeasuredClaudeCode()
    {
        WriteRenderingOnlySession("hc-measured-session-1", "claude-code", "harness-collection measured session");

        projections.ReplaceSessionProjection(new StoredSessionProjection(
            SessionId: "hc-measured-session-1",
            Label: "harness-collection measured session",
            IsSubagent: false,
            SpanCount: 2,
            StartedAtUtc: "2026-09-06T10:00:00.000Z",
            EndedAtUtc: "2026-09-06T10:05:00.000Z",
            ParentSessionId: null,
            AgentName: null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: [],
            Context: new StoredSessionContext(
                ContextLimit: 200000,
                ContextLimitSource: "reported",
                ResidualTotal: 0,
                FreshJumpFactor: 0,
                AccumulationRate: 0,
                ObservedBuckets: [],
                DerivedCounts: [],
                FlaggedTurnIndexes: [],
                Turns:
                [
                    new StoredSessionContextTurn(
                        Position: 0,
                        InputTokens: 84000,
                        BucketedTokens: 0,
                        ResidualTokens: 0,
                        ResidualAttribution: "reported",
                        BuiltinToolDefinitionTokens: 0,
                        FreshInput: 2000,
                        Buckets: [],
                        ServerDefinitions: [],
                        Headroom: 116000,
                        Pressure: 0.42),
                    new StoredSessionContextTurn(
                        Position: 1,
                        InputTokens: 130000,
                        BucketedTokens: 0,
                        ResidualTokens: 0,
                        ResidualAttribution: "reported",
                        BuiltinToolDefinitionTokens: 0,
                        FreshInput: 2000,
                        Buckets: [],
                        ServerDefinitions: [],
                        Headroom: 130000,
                        Pressure: 0.35),
                ])));
    }

    /// <summary>
    /// Bounds the zero-record seed: <c>droid</c> never gains a run, but its rendering-only session
    /// row must exist so the rollup projection emits its empty rollup and <c>/harness/droid</c>
    /// renders honest-empty instead of the missing-rollup 404.
    /// </summary>
    private void SeedZeroRecordDroid()
    {
        WriteRenderingOnlySession("hc-zero-record-session-1", "droid", "harness-collection zero-record session");

        projections.ReplaceSessionProjection(new StoredSessionProjection(
            SessionId: "hc-zero-record-session-1",
            Label: "harness-collection zero-record session",
            IsSubagent: false,
            SpanCount: 0,
            StartedAtUtc: "2026-09-06T12:00:00.000Z",
            EndedAtUtc: "2026-09-06T12:01:00.000Z",
            ParentSessionId: null,
            AgentName: null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: [],
            ContextUnmeasurableReason: "zero-record seed — context not measured"));
    }

    private void SeedRenderingOnlyOpencode()
    {
        WriteRenderingOnlySession("hc-honest-session-1", "opencode", "rendering-only seed — not capture proof");

        projections.ReplaceSessionProjection(new StoredSessionProjection(
            SessionId: "hc-honest-session-1",
            Label: "rendering-only seed — not capture proof",
            IsSubagent: false,
            SpanCount: 1,
            StartedAtUtc: "2026-09-06T11:00:00.000Z",
            EndedAtUtc: "2026-09-06T11:02:00.000Z",
            ParentSessionId: null,
            AgentName: null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: [],
            ContextUnmeasurableReason: "rendering-only seed — context not measured"));
    }

    /// <summary>Persists one rendering-only collected session row the projections can key into.</summary>
    /// <param name="sessionId">Canonical session identifier.</param>
    /// <param name="harness">Harness attributed to the session.</param>
    /// <param name="title">Session title.</param>
    private void WriteRenderingOnlySession(string sessionId, string harness, string title)
    {
        writer.WriteBatch(new CollectedBatch(
            Source: new CollectedSource(harness, SourceKind.Otlp, $"e2e-seed:{sessionId}"),
            Sessions: [new CollectedSession(sessionId, harness, sessionId, Workspace: null, Title: title, ReportedUsage: [])],
            Records: [],
            Problems: []));
    }

    private void UpsertExplicitSeedRunsAndExecutions()
    {
        projections.ReplaceRuns(
        [
            new StoredRun(
                RunId: "hc-measured-run-1",
                HarnessId: "claude-code",
                GroupingBasis: "explicit",
                GroupingRule: "explicit_run_id",
                ExecutionCount: 1,
                Label: "harness-collection measured run",
                StartedAtUtc: "2026-09-06T10:00:00.000Z",
                EndedAtUtc: "2026-09-06T10:05:00.000Z"),
            new StoredRun(
                RunId: "hc-honest-run-1",
                HarnessId: "opencode",
                GroupingBasis: "explicit",
                GroupingRule: "explicit_run_id",
                ExecutionCount: 1,
                Label: "harness-collection honest-empty run",
                StartedAtUtc: "2026-09-06T11:00:00.000Z",
                EndedAtUtc: "2026-09-06T11:02:00.000Z"),
        ]);

        projections.ReplaceExecutions(
        [
            new StoredExecution(
                ExecutionId: "hc-measured-exec-1",
                RunId: "hc-measured-run-1",
                HarnessId: "claude-code",
                IsRoot: true,
                SessionId: "hc-measured-session-1",
                StartedAtUtc: "2026-09-06T10:00:00.000Z",
                EndedAtUtc: "2026-09-06T10:05:00.000Z"),
            new StoredExecution(
                ExecutionId: "hc-honest-exec-1",
                RunId: "hc-honest-run-1",
                HarnessId: "opencode",
                IsRoot: true,
                SessionId: "hc-honest-session-1",
                StartedAtUtc: "2026-09-06T11:00:00.000Z",
                EndedAtUtc: "2026-09-06T11:02:00.000Z"),
        ]);
    }

    private static string ResolveFirstExisting(string primary, string fallback)
    {
        if (File.Exists(primary))
        {
            return primary;
        }

        if (File.Exists(fallback))
        {
            return fallback;
        }

        throw new FileNotFoundException(
            $"Could not locate OpenCode replay fixture at {primary} or {fallback}.",
            primary);
    }
}
