using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>Per-harness session and record counts observed by a full re-ingest pass.</summary>
/// <param name="HarnessId">Canonical harness identifier.</param>
/// <param name="Sessions">Number of normalized sessions attributed to <paramref name="HarnessId"/>.</param>
/// <param name="Records">Number of normalized telemetry records attributed to <paramref name="HarnessId"/>.</param>
public record HarnessReingestCounts(string HarnessId, int Sessions, int Records);

/// <summary>Summary returned by a full re-ingest pass over the refresh history window.</summary>
/// <param name="RunId">Refresh run audit identifier.</param>
/// <param name="HistoryWeeks">Lookback window in weeks applied to source discovery.</param>
/// <param name="CommandStartedAt">ISO-8601 UTC instant the pass started.</param>
/// <param name="Rows">Per-source outcome rows in discovery order.</param>
/// <param name="Harnesses">Per-harness session and record counts ordered by harness identifier.</param>
/// <param name="PurgedContentCount">Number of records whose content columns were cleared by the retention pass.</param>
/// <param name="ExitCode">Process exit code (<c>0</c> when every unit committed or skipped, <c>1</c> otherwise).</param>
public record FullReingestReport(
    string RunId,
    int HistoryWeeks,
    string CommandStartedAt,
    IReadOnlyList<RefreshSourceStatusRow> Rows,
    IReadOnlyList<HarnessReingestCounts> Harnesses,
    int PurgedContentCount,
    int ExitCode);

/// <summary>
/// Rolling content retention purge, checkpoint coverage arithmetic, and source refresh orchestrator
/// operating on <see cref="IRelationalStoreWriter"/>, <see cref="IRelationalStoreReader"/>, and
/// <see cref="IOperationalStore"/>.
/// </summary>
public class CanonRefreshEngine
{
    /// <summary>SQLite primary result code <c>SQLITE_CONSTRAINT</c> (UNIQUE, NOT NULL, FOREIGN KEY, CHECK).</summary>
    private const int SqliteConstraintResultCode = 19;

    /// <summary>
    /// Decides whether <paramref name="exception"/> belongs to one source unit, so the unit is recorded as a
    /// <c>SOURCE_UNREADABLE</c> problem and the pass continues. Read, parse, and argument failures qualify, as does a
    /// store write rejected by a SQLite constraint (<see cref="SqliteExecutionException.ExitCode"/> 19) or refused by
    /// the store's own row validation (<see cref="DashValidationException"/> with
    /// <see cref="SqliteRelationalStore.ConstraintViolatedCode"/> or <see cref="SqliteRelationalStore.JsonTextRefusedCode"/>),
    /// which concern only that unit's rows; any other validation code is a defect and still fails the pass. Failures that mean the store itself is unusable (busy or locked database,
    /// corrupt or unopenable file, missing native library, open circuit breaker) do not qualify and still fail the pass.
    /// </summary>
    /// <param name="exception">Exception raised while processing a single source unit.</param>
    /// <returns><see langword="true"/> when the failure is confined to the unit.</returns>
    public static bool IsSourceUnitFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or JsonException or ArgumentException
            || exception is SqliteExecutionException { ExitCode: SqliteConstraintResultCode }
            || exception is DashValidationException
            {
                Code: SqliteRelationalStore.ConstraintViolatedCode or SqliteRelationalStore.JsonTextRefusedCode,
            };
    }

    /// <summary>Rolling content retention window in days (decision D18).</summary>
    public const int ContentRetentionDays = 14;

    /// <summary>Default lookback window applied by source refresh passes in weeks.</summary>
    public const int DefaultHistoryWeeks = 2;

    /// <summary>Metadata key recording the retention window applied by the last purge pass.</summary>
    public const string ContentRetentionDaysKey = "content_retention_days";

    /// <summary>Metadata key recording the ISO-8601 UTC cutoff applied by the last purge pass.</summary>
    public const string ContentPurgedThroughKey = "content_purged_through";

    private readonly CanonSessionProjectorEngine? projector;
    private readonly CanonRollupsEngine? rollups;
    private readonly IRelationalStoreWriter? writer;
    private readonly IRelationalStoreReader? reader;
    private readonly IOperationalStore? operationalStore;
    private readonly HarnessSourceRegistry? sourceRegistry;
    private IReadOnlyDictionary<string, IHarnessMapper> mappersByHarness =
        new Dictionary<string, IHarnessMapper>(StringComparer.Ordinal);
    private IHarnessFileSystem fileSystem = PhysicalHarnessFileSystem.Instance;
    private string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string stateDirectory = ServerStateRepository.ResolveStateDirectory();
    private Func<DateTimeOffset> clock = static () => DateTimeOffset.UtcNow;

    /// <summary>Diagnostic code emitted when a local source unit cannot be read or parsed.</summary>
    public const string SourceUnreadableCode = "SOURCE_UNREADABLE";

    /// <summary>
    /// The key of the <see cref="SourceUnreadableCode"/> problem a failed source unit records, so the
    /// unit's next successful import can remove it.
    /// </summary>
    /// <param name="harnessId">Harness owning the source unit.</param>
    /// <param name="sourceKey">The source unit's key.</param>
    /// <returns>The problem key.</returns>
    public static string SourceUnreadableProblemKey(string harnessId, string sourceKey) =>
        string.Create(CultureInfo.InvariantCulture, $"source-unreadable:{harnessId}:{sourceKey}");

    /// <summary>
    /// Initializes a relational <see cref="CanonRefreshEngine"/> persisting batches and
    /// operational state exclusively through <see cref="IRelationalStoreWriter"/> and
    /// <see cref="IOperationalStore"/>.
    /// </summary>
    /// <param name="writer">Relational store writer receiving mapped batches and ingest problems.</param>
    /// <param name="reader">Relational store reader answering per-harness session and record counts.</param>
    /// <param name="operationalStore">Operational store managing checkpoints, refresh runs, and retention stamps.</param>
    /// <param name="sourceRegistry">Registry discovering and reading local harness source units.</param>
    /// <param name="mappers">Pure per-harness mappers translating native units to relational batches.</param>
    /// <param name="fileSystem">Filesystem seam used when reading local source units.</param>
    /// <param name="projector">Session projection engine refreshed after source commits.</param>
    /// <param name="rollups">Run and harness rollup engine refreshed after source commits.</param>
    /// <param name="options">Application options supplying state directory overrides.</param>
    /// <param name="homeDirectory">Optional home directory override for deterministic discovery.</param>
    /// <param name="clock">Optional UTC clock override for deterministic refresh timestamps.</param>
    public CanonRefreshEngine(
        IRelationalStoreWriter writer,
        IRelationalStoreReader reader,
        IOperationalStore operationalStore,
        HarnessSourceRegistry sourceRegistry,
        IReadOnlyList<IHarnessMapper>? mappers = null,
        IHarnessFileSystem? fileSystem = null,
        CanonSessionProjectorEngine? projector = null,
        CanonRollupsEngine? rollups = null,
        DashOptions? options = null,
        string? homeDirectory = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(operationalStore);
        ArgumentNullException.ThrowIfNull(sourceRegistry);

        this.projector = projector;
        this.rollups = rollups;

        this.writer = writer;
        this.reader = reader;
        this.operationalStore = operationalStore;
        this.sourceRegistry = sourceRegistry;
        mappersByHarness = HarnessMapperLookup.Build(mappers);
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
    /// Normalizes a persisted history coverage window according to "absent is not zero":
    /// only positive 32-bit integers are retained; non-positive or null values return <see langword="null"/>.
    /// </summary>
    /// <param name="value">Candidate history window in weeks.</param>
    /// <returns>The valid window, or <see langword="null"/> when <paramref name="value"/> is absent or out of range.</returns>
    public static int? NormalizeHistoryWeeks(long? value) =>
        value is > 0 and <= int.MaxValue ? (int)value.Value : null;

    /// <summary>Returns <paramref name="now"/> minus <see cref="ContentRetentionDays"/> days.</summary>
    /// <param name="now">Reference instant.</param>
    /// <returns>Retention floor instant.</returns>
    public static DateTimeOffset ContentRetentionFloor(DateTimeOffset now) =>
        now.AddDays(-ContentRetentionDays);

    /// <summary>Returns true when <paramref name="checkpoint"/> matches <paramref name="request"/> and was neither invalidated nor failed.</summary>
    /// <param name="checkpoint">Existing checkpoint row, if any.</param>
    /// <param name="request">Requested revision token and parser contract version.</param>
    /// <returns><see langword="true"/> when the checkpoint can be reused.</returns>
    public static bool CheckpointIsReusable(SourceCheckpoint? checkpoint, CoverageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (checkpoint is null)
        {
            return false;
        }

        if (string.Equals(checkpoint.LastStatus, "invalidated", StringComparison.Ordinal)
            || string.Equals(checkpoint.LastStatus, "failed", StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(checkpoint.ParserContractVersion, request.ParserContractVersion, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(checkpoint.RevisionToken, request.RevisionToken, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Computes intervals inside <paramref name="requested"/> not yet covered by a reusable
    /// <paramref name="checkpoint"/>.
    /// </summary>
    /// <param name="checkpoint">Existing source checkpoint, if any.</param>
    /// <param name="requested">Requested UTC coverage bounds.</param>
    /// <param name="request">Requested revision and parser contract.</param>
    /// <returns>Uncovered intervals requiring refresh.</returns>
    public static IReadOnlyList<CoverageInterval> UncoveredIntervals(
        SourceCheckpoint? checkpoint,
        CoverageInterval requested,
        CoverageRequest request)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(request);

        if (checkpoint is null || !CheckpointIsReusable(checkpoint, request))
        {
            return [requested];
        }

        return SubtractClosed(requested, new CoverageInterval(checkpoint.CoveredFromUtc, checkpoint.CoveredThroughUtc));
    }

    /// <summary>
    /// Runs a full re-ingest pass over the refresh history window: discovers every local source
    /// unit, replaces its previously persisted rows, maps and commits fresh batches through
    /// <see cref="IRelationalStoreWriter"/>, checkpoints each unit in <see cref="IOperationalStore"/>,
    /// stamps retention metadata, refreshes projections, and reports per-harness session and record
    /// counts read back through <see cref="IRelationalStoreReader"/>.
    /// </summary>
    /// <remarks>
    /// Unlike checkpoint-gated refresh, every discovered unit is reprocessed: its prior source rows
    /// are deleted before the fresh batch is written, so repeating the pass keeps counts stable
    /// instead of duplicating rows. Units that cannot be read or parsed fail their row with
    /// <see cref="SourceUnreadableCode"/> without halting the remaining units.
    /// </remarks>
    /// <param name="historyWeeks">Lookback window in weeks applied to source discovery.</param>
    /// <param name="trigger">Trigger label recorded on the audit row.</param>
    /// <param name="now">Optional reference instant (defaults to the configured clock).</param>
    /// <returns>Summary report including per-harness session and record counts.</returns>
    public FullReingestReport FullReingest(
        int historyWeeks = DefaultHistoryWeeks,
        string trigger = "reingest",
        DateTimeOffset? now = null)
    {
        IRelationalStoreWriter activeWriter = writer ?? throw new InvalidOperationException(
            "FullReingest requires the relational store constructor.");
        IRelationalStoreReader activeReader = reader ?? throw new InvalidOperationException(
            "FullReingest requires the relational store constructor.");
        IOperationalStore activeStore = operationalStore ?? throw new InvalidOperationException(
            "FullReingest requires the relational store constructor.");
        HarnessSourceRegistry activeRegistry = sourceRegistry ?? throw new InvalidOperationException(
            "FullReingest requires the relational store constructor.");
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        int normalizedWeeks = NormalizeHistoryWeeks(historyWeeks) ?? DefaultHistoryWeeks;
        DateTimeOffset commandStarted = now ?? clock();
        DateTimeOffset windowStart = commandStarted.AddDays(-7 * normalizedWeeks);
        string coveredFromUtc = FormatUtc(windowStart);
        string coveredThroughUtc = FormatUtc(commandStarted);
        string importedAtUtc = coveredThroughUtc;

        string runId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        activeStore.UpsertRefreshRun(new RefreshRunRow(
            Id: runId,
            StartedAt: importedAtUtc,
            CompletedAt: null,
            Status: "running",
            Pid: Environment.ProcessId,
            Trigger: trigger,
            Summary: null,
            HistoryWeeks: normalizedWeeks));

        bool runClosed = false;
        try
        {
            SourceDiscoveryContext context = new(
                HomeDirectory: homeDirectory,
                StateDirectory: stateDirectory,
                FileSystem: fileSystem,
                SinceUtc: windowStart);
            SourceDiscoveryResult discovery = activeRegistry.DiscoverAll(context);
            foreach (CollectedProblem discoveryProblem in discovery.Problems)
            {
                activeWriter.WriteProblem(discoveryProblem);
            }

            List<RefreshSourceStatusRow> rows = new(discovery.Sources.Count);
            foreach (SourceUnit unit in discovery.Sources)
            {
                if (activeRegistry.IsExcluded(unit.HarnessId))
                {
                    rows.Add(new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "skipped", 0));
                    continue;
                }

                rows.Add(ProcessFullReingestUnit(
                    unit,
                    commandStarted,
                    activeWriter,
                    activeReader,
                    activeStore,
                    coveredFromUtc,
                    coveredThroughUtc,
                    importedAtUtc));
            }

            string cutoffUtc = FormatUtc(ContentRetentionFloor(commandStarted));
            activeStore.SetContentRetentionDays(ContentRetentionDays);
            activeStore.SetContentPurgeMarkUtc(cutoffUtc);

            projector?.ProjectIncremental();
            rollups?.ProjectRunsAndHarnesses();

            int failedCount = rows.Count(static r => string.Equals(r.Status, "failed", StringComparison.Ordinal));
            int exitCode = failedCount > 0 ? 1 : 0;
            string summary = exitCode == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{rows.Count} source unit(s), {activeReader.Harnesses().Count} harness(es)")
                : string.Create(CultureInfo.InvariantCulture, $"{failedCount} of {rows.Count} source unit(s) failed");

            activeStore.UpsertRefreshRun(new RefreshRunRow(
                Id: runId,
                StartedAt: importedAtUtc,
                CompletedAt: FormatUtc(clock()),
                Status: exitCode == 0 ? "success" : "failure",
                Pid: Environment.ProcessId,
                Trigger: trigger,
                Summary: summary,
                HistoryWeeks: normalizedWeeks));
            runClosed = true;

            List<HarnessReingestCounts> harnesses = [.. activeReader.Harnesses()
                .OrderBy(static h => h, StringComparer.Ordinal)
                .Select(h => new HarnessReingestCounts(
                    h,
                    activeReader.Sessions(h).Count,
                    activeReader.CountRecords(h)))];

            return new FullReingestReport(
                RunId: runId,
                HistoryWeeks: normalizedWeeks,
                CommandStartedAt: importedAtUtc,
                Rows: rows,
                Harnesses: harnesses,
                PurgedContentCount: 0,
                ExitCode: exitCode);
        }
        catch (Exception ex) when (!runClosed)
        {
            try
            {
                activeStore.UpsertRefreshRun(new RefreshRunRow(
                    Id: runId,
                    StartedAt: importedAtUtc,
                    CompletedAt: FormatUtc(clock()),
                    Status: "failure",
                    Pid: Environment.ProcessId,
                    Trigger: trigger,
                    Summary: SafeProblemMessage(ex.Message, "Source re-ingest failed."),
                    HistoryWeeks: normalizedWeeks));
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private RefreshSourceStatusRow ProcessFullReingestUnit(
        SourceUnit unit,
        DateTimeOffset commandStarted,
        IRelationalStoreWriter activeWriter,
        IRelationalStoreReader activeReader,
        IOperationalStore activeStore,
        string coveredFromUtc,
        string coveredThroughUtc,
        string importedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(unit);

        IHarnessSourceReader? sourceReader = sourceRegistry!.GetReader(unit.HarnessId);
        IHarnessMapper? mapper = HarnessMapperLookup.Resolve(mappersByHarness, unit.HarnessId);
        if (sourceReader is null || mapper is null)
        {
            return new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "skipped", 0);
        }

        SourceCheckpoint? existing = activeStore.GetSourceCheckpoint(unit.HarnessId, unit.SourceKey);

        try
        {
            NativeSourceUnit? nativeUnit = sourceReader.Read(unit, fileSystem);
            if (nativeUnit is null)
            {
                throw new FileNotFoundException(
                    string.Create(CultureInfo.InvariantCulture, $"Source unit '{unit.SourceKey}' could not be read."),
                    unit.SourceKey);
            }

            CollectedBatch batch = mapper.Map(nativeUnit, commandStarted);

            // Clear the unit's earlier failure before writing: if anything below fails, the catch records it
            // again, so no committed write is ever paired with a stale failure, nor a failure with no problem.
            activeWriter.DeleteProblem(SourceUnreadableProblemKey(unit.HarnessId, unit.SourceKey));

            string? existingSourceId = activeReader.Sources(unit.HarnessId)
                .FirstOrDefault(s => string.Equals(s.Source.Locator, unit.SourceKey, StringComparison.Ordinal))?.SourceId;
            if (existingSourceId is not null)
            {
                // One transaction: a refused write keeps the stored rows, including spans shared with another source.
                activeWriter.ReplaceSource(batch);
            }
            else
            {
                activeWriter.WriteBatch(batch);
            }
            foreach (CollectedProblem batchProblem in batch.Problems)
            {
                activeWriter.WriteProblem(batchProblem);
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

            activeStore.UpsertSourceCheckpoint(updatedCheckpoint);
            return new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "committed", batch.Records.Count);
        }
        catch (Exception ex) when (CanonRefreshEngine.IsSourceUnitFailure(ex))
        {
            string safeMessage = SafeProblemMessage(
                ex.Message,
                string.Create(CultureInfo.InvariantCulture, $"Failed to read or parse source unit '{unit.SourceKey}'."));

            activeWriter.WriteProblem(new CollectedProblem(
                ProblemKey: SourceUnreadableProblemKey(unit.HarnessId, unit.SourceKey),
                Severity: "error",
                Code: SourceUnreadableCode,
                Message: safeMessage,
                SpanId: null,
                Location: string.IsNullOrWhiteSpace(unit.SourceKey) ? null : unit.SourceKey,
                SessionKey: null,
                HarnessId: unit.HarnessId,
                TimestampUtc: importedAtUtc,
                Stack: SafeOptionalText(ex.StackTrace)));

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

            activeStore.UpsertSourceCheckpoint(failedCheckpoint);
            return new RefreshSourceStatusRow(unit.HarnessId, unit.SourceKey, "failed", 0, safeMessage);
        }
    }

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string SafeProblemMessage(string? message, string fallback) =>
        string.IsNullOrWhiteSpace(message) || JsonDocumentText.IsDocument(message) ? fallback : message;

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || JsonDocumentText.IsDocument(value) ? null : value;

    private static List<CoverageInterval> SubtractClosed(CoverageInterval requested, CoverageInterval covered)
    {
        List<CoverageInterval> gaps = [];
        if (string.CompareOrdinal(requested.FromUtc, covered.FromUtc) < 0)
        {
            string end = string.CompareOrdinal(requested.ThroughUtc, covered.FromUtc) < 0
                ? requested.ThroughUtc
                : covered.FromUtc;
            gaps.Add(new CoverageInterval(requested.FromUtc, end));
        }

        if (string.CompareOrdinal(requested.ThroughUtc, covered.ThroughUtc) > 0)
        {
            string start = string.CompareOrdinal(requested.FromUtc, covered.ThroughUtc) > 0
                ? requested.FromUtc
                : covered.ThroughUtc;
            gaps.Add(new CoverageInterval(start, requested.ThroughUtc));
        }

        return gaps;
    }
}
