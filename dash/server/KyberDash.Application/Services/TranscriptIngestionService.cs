using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Canonical span normalization and local transcript ingestion backed by
/// <see cref="IRelationalStoreWriter"/>, <see cref="IOperationalStore"/>, and
/// <see cref="HarnessSourceRegistry"/>.
/// </summary>
/// <remarks>
/// Every write path into normalized telemetry tables goes through this service: OTLP admission,
/// local harness source refresh, and retention checkpointing. Retained transitional support for
/// <see cref="ICanonStoreRepository"/> remains until remaining legacy callers finish migrating.
/// </remarks>
public class TranscriptIngestionService : ITranscriptIngestionService
{
    /// <summary>Diagnostic code emitted when a local source unit cannot be read or parsed.</summary>
    public const string SourceUnreadableCode = "SOURCE_UNREADABLE";

    private readonly IRelationalStoreWriter? writer;
    private readonly IOperationalStore? operationalStore;
    private readonly HarnessSourceRegistry? sourceRegistry;
    private readonly IReadOnlyDictionary<string, IHarnessMapper> mappersByHarness;
    private readonly IHarnessFileSystem fileSystem;
    private readonly string homeDirectory;
    private readonly string stateDirectory;
    private readonly Func<DateTimeOffset> clock;

    private readonly ICanonStoreRepository? legacyStore;
    private readonly ITranscriptReaderRepository? legacyTranscripts;
    private readonly CanonRefreshEngine? legacyRefresh;

    /// <summary>
    /// Initializes a relational <see cref="TranscriptIngestionService"/> persisting batches and
    /// operational state exclusively through <see cref="IRelationalStoreWriter"/> and
    /// <see cref="IOperationalStore"/>.
    /// </summary>
    /// <param name="writer">Relational store writer receiving mapped batches and ingest problems.</param>
    /// <param name="operationalStore">Operational store managing checkpoints, refresh runs, retention stamps, and quarantine.</param>
    /// <param name="sourceRegistry">Registry discovering and reading local harness source units.</param>
    /// <param name="mappers">Pure per-harness mappers translating native units to relational batches.</param>
    /// <param name="fileSystem">Filesystem seam used when reading local source units.</param>
    /// <param name="options">Application options supplying state directory overrides.</param>
    /// <param name="homeDirectory">Optional home directory override for deterministic discovery.</param>
    /// <param name="clock">Optional UTC clock override for deterministic refresh timestamps.</param>
    public TranscriptIngestionService(
        IRelationalStoreWriter writer,
        IOperationalStore operationalStore,
        HarnessSourceRegistry sourceRegistry,
        IReadOnlyList<IHarnessMapper>? mappers = null,
        IHarnessFileSystem? fileSystem = null,
        DashOptions? options = null,
        string? homeDirectory = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(sourceRegistry);

        this.writer = writer;
        this.operationalStore = operationalStore;
        this.sourceRegistry = sourceRegistry;
        mappersByHarness = BuildMapperLookup(mappers);
        this.fileSystem = fileSystem ?? PhysicalHarnessFileSystem.Instance;

        bool explicitHome = !string.IsNullOrWhiteSpace(homeDirectory);
        this.homeDirectory = explicitHome
            ? Path.GetFullPath(homeDirectory!)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        this.stateDirectory = !string.IsNullOrWhiteSpace(options?.StateDirectory)
            ? Path.GetFullPath(options.StateDirectory)
            : explicitHome
                ? Path.Combine(this.homeDirectory, CanonDbPath.DefaultDirectoryName)
                : ServerStateRepository.ResolveStateDirectory();

        this.clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Initializes a transitional <see cref="TranscriptIngestionService"/> over
    /// <see cref="ICanonStoreRepository"/> for callers not yet migrated to the relational contracts.
    /// </summary>
    /// <param name="store">Canonical store repository receiving records and checkpoints.</param>
    /// <param name="transcripts">Transcript reader repository discovering local sources.</param>
    /// <param name="refresh">Refresh engine owning deduplication, checkpoints, and retention.</param>
    public TranscriptIngestionService(
        ICanonStoreRepository store,
        ITranscriptReaderRepository transcripts,
        CanonRefreshEngine refresh)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(refresh);

        legacyStore = store;
        legacyTranscripts = transcripts;
        legacyRefresh = refresh;
        mappersByHarness = new Dictionary<string, IHarnessMapper>(StringComparer.Ordinal);
        fileSystem = PhysicalHarnessFileSystem.Instance;
        homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        stateDirectory = ServerStateRepository.ResolveStateDirectory();
        clock = static () => DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public IngestOutcome IngestSpans(IReadOnlyList<OtlpSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        if (spans.Count == 0)
        {
            return new IngestOutcome(0, 0, 0);
        }

        if (writer is not null && operationalStore is not null)
        {
            return CanonIngestPipeline.IngestBatch(spans, writer, operationalStore);
        }

        return CanonIngestPipeline.IngestBatch(spans, legacyStore!);
    }

    /// <inheritdoc />
    public RefreshReport RefreshHarnessSources(int historyWeeks = 2)
    {
        if (legacyRefresh is not null && legacyTranscripts is not null)
        {
            int legacyWeeks = CanonSchema.NormalizeHistoryWeeks(historyWeeks) ?? CanonRefreshEngine.DefaultHistoryWeeks;
            DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-7.0 * legacyWeeks);
            IReadOnlyList<RefreshSourceInput> sources = legacyTranscripts.DiscoverLocalSources(since);
            return legacyRefresh.RefreshHarnessSources(sources, legacyWeeks, trigger: "refresh");
        }

        return RefreshRelationalSources(historyWeeks);
    }

    /// <inheritdoc />
    public PurgeExpiredContentResult PurgeExpiredContent(DateTimeOffset? now = null)
    {
        if (legacyRefresh is not null)
        {
            return legacyRefresh.PurgeExpiredContent(now);
        }

        DateTimeOffset instant = now ?? clock();
        string cutoffUtc = FormatUtc(CanonRefreshEngine.ContentRetentionFloor(instant));
        operationalStore!.SetContentRetentionDays(CanonRefreshEngine.ContentRetentionDays);
        operationalStore.SetContentPurgeMarkUtc(cutoffUtc);
        return new PurgeExpiredContentResult(0, cutoffUtc);
    }

    /// <summary>Records a transcript refresh in the audit table under an explicit trigger label.</summary>
    /// <param name="sources">Declarative refreshable source units.</param>
    /// <param name="historyWeeks">History lookback in weeks.</param>
    /// <param name="trigger">Trigger label stored on the audit row.</param>
    /// <returns>The refresh report.</returns>
    public RefreshReport IngestSources(
        IReadOnlyList<RefreshSourceInput> sources,
        int historyWeeks,
        string trigger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (legacyRefresh is null)
        {
            throw new NotSupportedException(
                "IngestSources(RefreshSourceInput) is only supported on the transitional legacy store path; use RefreshHarnessSources.");
        }

        return legacyRefresh.RefreshHarnessSources(sources, historyWeeks, trigger);
    }

    /// <summary>Retention floor applied by <see cref="PurgeExpiredContent"/>.</summary>
    /// <param name="now">Reference instant.</param>
    /// <returns>The instant before which record content is cleared.</returns>
    public static DateTimeOffset RetentionFloor(DateTimeOffset now) =>
        CanonRefreshEngine.ContentRetentionFloor(now);

    /// <summary>The retention floor for the current instant, formatted as the purge cutoff.</summary>
    /// <returns>ISO-8601 UTC cutoff.</returns>
    public static string RetentionFloorIso() =>
        FormatUtc(CanonRefreshEngine.ContentRetentionFloor(DateTimeOffset.UtcNow));

    private RefreshReport RefreshRelationalSources(int historyWeeks)
    {
        int weeks = CanonSchema.NormalizeHistoryWeeks(historyWeeks) ?? CanonRefreshEngine.DefaultHistoryWeeks;
        DateTimeOffset commandStarted = clock();
        DateTimeOffset windowStart = commandStarted.AddDays(-7.0 * weeks);
        string coveredFromUtc = FormatUtc(windowStart);
        string coveredThroughUtc = FormatUtc(commandStarted);
        string importedAtUtc = coveredThroughUtc;

        string runId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        operationalStore!.UpsertRefreshRun(new RefreshRunRow(
            Id: runId,
            StartedAt: importedAtUtc,
            CompletedAt: null,
            Status: "running",
            Pid: Environment.ProcessId,
            Trigger: "refresh",
            Summary: null,
            HistoryWeeks: weeks));

        bool runClosed = false;
        try
        {
            SourceDiscoveryContext context = new(
                HomeDirectory: homeDirectory,
                StateDirectory: stateDirectory,
                FileSystem: fileSystem,
                SinceUtc: windowStart);
            SourceDiscoveryResult discovery = sourceRegistry!.DiscoverAll(context);
            foreach (CollectedProblem discoveryProblem in discovery.Problems)
            {
                writer!.WriteProblem(discoveryProblem);
            }

            List<RefreshSourceStatusRow> rows = new(discovery.Sources.Count);
            HashSet<string> sessionKeys = new(StringComparer.Ordinal);

            foreach (SourceUnit unit in discovery.Sources)
            {
                if (sourceRegistry.IsExcluded(unit.HarnessId))
                {
                    rows.Add(new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "skipped", 0));
                    continue;
                }

                rows.Add(ProcessRelationalSourceUnit(
                    unit,
                    commandStarted,
                    coveredFromUtc,
                    coveredThroughUtc,
                    importedAtUtc,
                    sessionKeys));
            }

            PurgeExpiredContentResult purgeResult = PurgeExpiredContent(commandStarted);

            int failedCount = rows.Count(static r => string.Equals(r.Status, "failed", StringComparison.Ordinal));
            int exitCode = failedCount > 0 ? 1 : 0;
            string summary = exitCode == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{rows.Count} source unit(s), {sessionKeys.Count} session(s)")
                : string.Create(CultureInfo.InvariantCulture, $"{failedCount} of {rows.Count} source unit(s) failed");

            operationalStore.UpsertRefreshRun(new RefreshRunRow(
                Id: runId,
                StartedAt: importedAtUtc,
                CompletedAt: FormatUtc(clock()),
                Status: exitCode == 0 ? "success" : "failure",
                Pid: Environment.ProcessId,
                Trigger: "refresh",
                Summary: summary,
                HistoryWeeks: weeks));
            runClosed = true;

            return new RefreshReport(
                RunId: runId,
                HistoryWeeks: weeks,
                CommandStartedAt: importedAtUtc,
                Rows: rows,
                PurgedContentCount: purgeResult.Purged,
                Sessions: sessionKeys.Count,
                Runs: 0,
                Executions: 0,
                Rollups: 0,
                ExitCode: exitCode);
        }
        catch (Exception ex) when (!runClosed)
        {
            string safeSummary = SafeProblemMessage(ex.Message, "Source refresh failed.");
            try
            {
                operationalStore.UpsertRefreshRun(new RefreshRunRow(
                    Id: runId,
                    StartedAt: importedAtUtc,
                    CompletedAt: FormatUtc(clock()),
                    Status: "failure",
                    Pid: Environment.ProcessId,
                    Trigger: "refresh",
                    Summary: safeSummary,
                    HistoryWeeks: weeks));
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private RefreshSourceStatusRow ProcessRelationalSourceUnit(
        SourceUnit unit,
        DateTimeOffset commandStarted,
        string coveredFromUtc,
        string coveredThroughUtc,
        string importedAtUtc,
        HashSet<string> sessionKeys)
    {
        SourceCheckpoint? existing = operationalStore!.GetSourceCheckpoint(unit.HarnessId, unit.SourceKey);
        CoverageInterval requested = new(coveredFromUtc, coveredThroughUtc);
        CoverageRequest request = new(unit.RevisionToken, unit.ParserContractVersion);

        if (CanonRefreshEngine.CheckpointIsReusable(existing, request)
            && CanonRefreshEngine.UncoveredIntervals(existing, requested, request).Count == 0)
        {
            return new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "skipped", 0);
        }

        IHarnessSourceReader? reader = sourceRegistry!.GetReader(unit.HarnessId);
        IHarnessMapper? mapper = ResolveMapper(unit.HarnessId);
        if (reader is null || mapper is null)
        {
            return new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "skipped", 0);
        }

        try
        {
            NativeSourceUnit? nativeUnit = reader.Read(unit, fileSystem);
            if (nativeUnit is null)
            {
                throw new FileNotFoundException(
                    string.Create(CultureInfo.InvariantCulture, $"Source unit '{unit.SourceKey}' could not be read."),
                    unit.SourceKey);
            }

            CollectedBatch batch = mapper.Map(nativeUnit, commandStarted);
            if (existing?.LastSuccessUtc is not null)
            {
                writer!.DeleteSource(ComputeSourceId(batch.Source));
            }

            writer!.WriteBatch(batch);

            foreach (CollectedSession session in batch.Sessions)
            {
                if (!string.IsNullOrWhiteSpace(session.SessionKey))
                {
                    _ = sessionKeys.Add(session.SessionKey);
                }
            }

            SourceCheckpoint updatedCheckpoint = new(
                HarnessId: unit.HarnessId,
                SourceKey: unit.SourceKey,
                ProviderId: unit.ProviderId,
                ParserId: unit.ParserId,
                ParserContractVersion: unit.ParserContractVersion,
                Format: unit.Format,
                SourceRootLabel: unit.SourceRootLabel,
                RevisionToken: unit.RevisionToken,
                CoveredFromUtc: coveredFromUtc,
                CoveredThroughUtc: coveredThroughUtc,
                LastAttemptUtc: importedAtUtc,
                LastSuccessUtc: importedAtUtc,
                LastStatus: "ok",
                LastErrorCode: null,
                UnitCount: 1,
                RecordCount: batch.Records.Count);

            operationalStore.UpsertSourceCheckpoint(updatedCheckpoint);
            return new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "committed", batch.Records.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or JsonException or ArgumentException)
        {
            string safeMessage = SafeProblemMessage(
                ex.Message,
                string.Create(CultureInfo.InvariantCulture, $"Failed to read or parse source unit '{unit.SourceKey}'."));

            CollectedProblem problem = new(
                ProblemKey: string.Create(CultureInfo.InvariantCulture, $"source-unreadable:{unit.HarnessId}:{unit.SourceKey}"),
                Severity: "error",
                Code: SourceUnreadableCode,
                Message: safeMessage,
                SpanId: null,
                Location: string.IsNullOrWhiteSpace(unit.SourceKey) ? null : unit.SourceKey,
                SessionKey: null,
                HarnessId: unit.HarnessId,
                TimestampUtc: importedAtUtc,
                Stack: SafeOptionalText(ex.StackTrace));

            writer!.WriteProblem(problem);

            SourceCheckpoint failedCheckpoint = new(
                HarnessId: unit.HarnessId,
                SourceKey: unit.SourceKey,
                ProviderId: unit.ProviderId,
                ParserId: unit.ParserId,
                ParserContractVersion: unit.ParserContractVersion,
                Format: unit.Format,
                SourceRootLabel: unit.SourceRootLabel,
                RevisionToken: unit.RevisionToken,
                CoveredFromUtc: existing?.CoveredFromUtc ?? coveredFromUtc,
                CoveredThroughUtc: existing?.CoveredThroughUtc ?? coveredThroughUtc,
                LastAttemptUtc: importedAtUtc,
                LastSuccessUtc: existing?.LastSuccessUtc,
                LastStatus: "failed",
                LastErrorCode: SourceUnreadableCode,
                UnitCount: 0,
                RecordCount: 0);

            operationalStore.UpsertSourceCheckpoint(failedCheckpoint);
            return new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "failed", 0, safeMessage);
        }
    }

    private IHarnessMapper? ResolveMapper(string harnessId)
    {
        if (mappersByHarness.TryGetValue(harnessId, out IHarnessMapper? direct))
        {
            return direct;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(harnessId);
        return mappersByHarness.GetValueOrDefault(normalized);
    }

    private static Dictionary<string, IHarnessMapper> BuildMapperLookup(IReadOnlyList<IHarnessMapper>? mappers)
    {
        Dictionary<string, IHarnessMapper> lookup = new(StringComparer.Ordinal);
        if (mappers is null)
        {
            return lookup;
        }

        foreach (IHarnessMapper mapper in mappers)
        {
            ArgumentNullException.ThrowIfNull(mapper);
            lookup[mapper.HarnessId] = mapper;
            string normalized = HarnessSourceRegistry.NormalizeHarnessId(mapper.HarnessId);
            _ = lookup.TryAdd(normalized, mapper);
        }

        return lookup;
    }

    private static string ComputeSourceId(CollectedSource source)
    {
        string kindText = source.Kind switch
        {
            SourceKind.File => "file",
            SourceKind.Otlp => "otlp",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unsupported source kind."),
        };

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{source.HarnessId}\0{kindText}\0{source.Locator}"));
        return $"src-{Convert.ToHexStringLower(digest)[..16]}";
    }

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string SafeProblemMessage(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) || JsonDocumentText.IsDocument(message) ? fallback : message;

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;
}
