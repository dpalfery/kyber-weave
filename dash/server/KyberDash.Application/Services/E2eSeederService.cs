using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Seeds an isolated canonical store for a Playwright world through the production ingest,
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

    private readonly ITranscriptIngestionService ingestion;
    private readonly ICanonProjectionService projection;
    private readonly ICanonStoreRepository store;

    /// <summary>
    /// Initializes a new <see cref="E2eSeederService"/>.
    /// </summary>
    /// <param name="ingestion">Ingestion service accepting the replayed spans.</param>
    /// <param name="projection">Projection service rebuilding sessions and rollups.</param>
    /// <param name="store">Canonical store repository receiving the rendering-only rows.</param>
    public E2eSeederService(
        ITranscriptIngestionService ingestion,
        ICanonProjectionService projection,
        ICanonStoreRepository store)
    {
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(store);

        this.ingestion = ingestion;
        this.projection = projection;
        this.store = store;
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

        store.EnsureInitialized();
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

        _ = projection.ProjectRunsAndHarnesses();

        UpsertExplicitSeedRunsAndExecutions();
        return 0;
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
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            id = "hc-measured-session-1",
            session_id = "hc-measured-session-1",
            harness = "claude-code",
            context = new
            {
                measurable = true,
                contextLimit = 200000,
                contextLimitSource = "reported",
                turns = new object[]
                {
                    new { pressure = 0.42, headroom = 116000 },
                    new { pressure = 0.35, headroom = 130000 },
                },
            },
            summary = new
            {
                turn_count = 2,
                total_input = 2000,
                total_output = 500,
                total_cache_read = 800,
                cost = new { status = "priced", value = 0.012, basis = "published", currency = "USD" },
            },
        });

        store.PutSession(new SessionSummaryRow(
            SessionId: "hc-measured-session-1",
            Harness: "claude-code",
            Label: "harness-collection measured session",
            IsSubagent: false,
            ParentSession: null,
            AgentName: null,
            Repo: null,
            Branch: null,
            Started: "2026-09-06T10:00:00.000Z",
            Ended: "2026-09-06T10:05:00.000Z",
            SummaryJson: payload.GetProperty("summary").GetRawText(),
            ProblemsCount: 0,
            ContextLimitSource: "reported",
            Payload: payload));
    }

    private void SeedRenderingOnlyOpencode()
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            id = "hc-honest-session-1",
            session_id = "hc-honest-session-1",
            harness = "opencode",
            context = new { measurable = false },
            summary = new { turn_count = 1 },
        });

        store.PutSession(new SessionSummaryRow(
            SessionId: "hc-honest-session-1",
            Harness: "opencode",
            Label: "rendering-only seed — not capture proof",
            IsSubagent: false,
            ParentSession: null,
            AgentName: null,
            Repo: null,
            Branch: null,
            Started: "2026-09-06T11:00:00.000Z",
            Ended: "2026-09-06T11:02:00.000Z",
            SummaryJson: payload.GetProperty("summary").GetRawText(),
            ProblemsCount: 0,
            ContextLimitSource: null,
            Payload: payload));
    }

    private void UpsertExplicitSeedRunsAndExecutions()
    {
        JsonElement measuredLinkage = JsonSerializer.SerializeToElement(new { status = "measured" });

        store.UpsertRuns(
        [
            new RunRow(
                RunId: "hc-measured-run-1",
                Harness: "claude-code",
                Label: "harness-collection measured run",
                GroupingBasis: "explicit",
                GroupingRule: null,
                WorkingDirectory: null,
                Started: "2026-09-06T10:00:00.000Z",
                Ended: "2026-09-06T10:05:00.000Z",
                ExecutionCount: 1),
            new RunRow(
                RunId: "hc-honest-run-1",
                Harness: "opencode",
                Label: "harness-collection honest-empty run",
                GroupingBasis: "explicit",
                GroupingRule: null,
                WorkingDirectory: null,
                Started: "2026-09-06T11:00:00.000Z",
                Ended: "2026-09-06T11:02:00.000Z",
                ExecutionCount: 1),
        ]);

        store.UpsertExecutions(
        [
            new ExecutionRow(
                ExecutionId: "hc-measured-exec-1",
                RunId: "hc-measured-run-1",
                SessionId: "hc-measured-session-1",
                ParentExecutionId: null,
                Harness: "claude-code",
                AgentName: null,
                IsRoot: true,
                Started: "2026-09-06T10:00:00.000Z",
                Ended: "2026-09-06T10:05:00.000Z",
                ParentLinkage: measuredLinkage),
            new ExecutionRow(
                ExecutionId: "hc-honest-exec-1",
                RunId: "hc-honest-run-1",
                SessionId: "hc-honest-session-1",
                ParentExecutionId: null,
                Harness: "opencode",
                AgentName: null,
                IsRoot: true,
                Started: "2026-09-06T11:00:00.000Z",
                Ended: "2026-09-06T11:02:00.000Z",
                ParentLinkage: measuredLinkage),
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
