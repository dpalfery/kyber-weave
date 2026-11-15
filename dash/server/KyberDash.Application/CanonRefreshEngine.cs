using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Rolling content retention purge, checkpoint coverage arithmetic, and source refresh orchestrator
/// operating on <see cref="ICanonStoreRepository"/>.
/// </summary>
public class CanonRefreshEngine
{
    /// <summary>Rolling content retention window in days (decision D18).</summary>
    public const int ContentRetentionDays = 14;

    /// <summary>Default lookback window applied by <see cref="RefreshHarnessSources(IReadOnlyList{RefreshSourceInput}, int, string)"/> in weeks.</summary>
    public const int DefaultHistoryWeeks = 2;

    /// <summary>Metadata key recording the retention window applied by the last purge pass.</summary>
    public const string ContentRetentionDaysKey = "content_retention_days";

    /// <summary>Metadata key recording the ISO-8601 UTC cutoff applied by the last purge pass.</summary>
    public const string ContentPurgedThroughKey = "content_purged_through";

    private readonly ICanonStoreRepository store;
    private readonly IPricingRepository pricing;
    private readonly CanonSessionProjectorEngine projector;
    private readonly CanonRollupsEngine rollups;

    /// <summary>
    /// Initializes a new <see cref="CanonRefreshEngine"/>.
    /// </summary>
    /// <param name="store">Canonical store repository.</param>
    /// <param name="transcripts">Transcript reader repository discovering local sources.</param>
    /// <param name="pricing">Model rate-table repository.</param>
    /// <param name="projector">Session projector engine refreshed after source commits.</param>
    /// <param name="rollups">Rollup engine refreshed after source commits.</param>
    public CanonRefreshEngine(
        ICanonStoreRepository store,
        ITranscriptReaderRepository transcripts,
        IPricingRepository pricing,
        CanonSessionProjectorEngine projector,
        CanonRollupsEngine rollups)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transcripts);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(rollups);

        this.store = store;
        this.pricing = pricing;
        this.projector = projector;
        this.rollups = rollups;
    }

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
    /// Empties <c>content_json</c> and <c>parts_json</c> in place on records older than 14 days
    /// while preserving rows and <c>raw</c> compressed payloads, and stamps retention metadata.
    /// </summary>
    /// <param name="now">Optional UTC reference instant.</param>
    /// <returns>Summary of purged records and applied cutoff timestamp.</returns>
    public PurgeExpiredContentResult PurgeExpiredContent(DateTimeOffset? now = null)
    {
        DateTimeOffset instant = now ?? DateTimeOffset.UtcNow;
        string cutoffUtc = ContentRetentionFloor(instant).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        int purged = store.PurgeContentBefore(cutoffUtc);
        store.SetMetadata(ContentRetentionDaysKey, ContentRetentionDays.ToString(CultureInfo.InvariantCulture));
        store.SetMetadata(ContentPurgedThroughKey, cutoffUtc);

        return new PurgeExpiredContentResult(purged, cutoffUtc);
    }

    /// <summary>
    /// Orchestrates one refresh pass using the current UTC clock: reconciles stale <c>running</c>
    /// rows, opens a <c>refresh_run</c> row, skips reusable checkpoints, synthesizes and deduplicates
    /// changed source units against OTLP spans, commits them atomically, enforces 14-day content
    /// retention, and projects sessions/runs/harness rollups.
    /// </summary>
    /// <param name="sources">Declarative refreshable source units.</param>
    /// <param name="historyWeeks">Lookback window in weeks.</param>
    /// <param name="trigger">Trigger label recorded on the audit row.</param>
    /// <returns>Summary report for the refresh pass.</returns>
    public RefreshReport RefreshHarnessSources(
        IReadOnlyList<RefreshSourceInput> sources,
        int historyWeeks = DefaultHistoryWeeks,
        string trigger = "cli") =>
        RefreshHarnessSources(sources, now: null, historyWeeks, trigger);

    /// <summary>
    /// Orchestrates one refresh pass with an explicit optional reference timestamp.
    /// </summary>
    /// <param name="sources">Declarative refreshable source units.</param>
    /// <param name="now">Optional reference instant (defaults to <see cref="DateTimeOffset.UtcNow"/>).</param>
    /// <param name="historyWeeks">Lookback window in weeks.</param>
    /// <param name="trigger">Trigger label recorded on the audit row.</param>
    /// <returns>Summary report for the refresh pass.</returns>
    public RefreshReport RefreshHarnessSources(
        IReadOnlyList<RefreshSourceInput> sources,
        DateTimeOffset? now,
        int historyWeeks = DefaultHistoryWeeks,
        string trigger = "cli")
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        _ = pricing.LoadPricingTables();

        DateTimeOffset commandStarted = now ?? DateTimeOffset.UtcNow;
        int normalizedWeeks = CanonSchema.NormalizeHistoryWeeks(historyWeeks) ?? DefaultHistoryWeeks;
        DateTimeOffset windowStart = commandStarted.AddDays(-7 * normalizedWeeks);

        string coveredFromUtc = windowStart.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string coveredThroughUtc = commandStarted.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        string importedAtUtc = coveredThroughUtc;

        store.ReconcileDeadRefreshRuns(importedAtUtc);
        string runId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        store.StartRefreshRun(runId, trigger, importedAtUtc, Environment.ProcessId, normalizedWeeks);

        bool runClosed = false;
        try
        {
            IReadOnlyList<CanonicalRecord> existingOtlp = store.ListOtlpRecords();
            HashSet<string> existingOtlpSpanIds = new(existingOtlp.Select(static r => r.SpanId), StringComparer.Ordinal);
            List<RefreshSourceStatusRow> rows = new(sources.Count);

            foreach (RefreshSourceInput source in sources)
            {
                ArgumentNullException.ThrowIfNull(source);
                if (CanonSynth.IsExcludedHarness(source.HarnessId))
                {
                    rows.Add(new RefreshSourceStatusRow(source.HarnessId, source.SourceKey, "skipped", 0));
                    continue;
                }

                rows.Add(ProcessSourceUnit(
                    source,
                    existingOtlp,
                    existingOtlpSpanIds,
                    coveredFromUtc,
                    coveredThroughUtc,
                    importedAtUtc));
            }

            PurgeExpiredContentResult purgeResult = PurgeExpiredContent(commandStarted);
            projector.ProjectIncremental();
            rollups.ProjectRunsAndHarnesses();

            int failedCount = rows.Count(static r => string.Equals(r.Status, "failed", StringComparison.Ordinal));
            int exitCode = failedCount > 0 ? 1 : 0;

            int sessionCount = store.ListSessions().Count;
            int runCount = store.ListRuns().Count;
            int executionCount = store.ListObservedHarnesses().Sum(h => store.ListExecutionsByHarness(h).Count);
            int rollupCount = store.ListHarnessRollups().Count;

            string summary = exitCode == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{rows.Count} source unit(s), {sessionCount} session(s)")
                : string.Create(CultureInfo.InvariantCulture, $"{failedCount} of {rows.Count} source unit(s) failed");

            store.CompleteRefreshRun(
                runId,
                exitCode == 0 ? "success" : "failure",
                DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                summary);
            runClosed = true;

            return new RefreshReport(
                RunId: runId,
                HistoryWeeks: normalizedWeeks,
                CommandStartedAt: importedAtUtc,
                Rows: rows,
                PurgedContentCount: purgeResult.Purged,
                Sessions: sessionCount,
                Runs: runCount,
                Executions: executionCount,
                Rollups: rollupCount,
                ExitCode: exitCode);
        }
        catch (Exception ex) when (!runClosed)
        {
            try
            {
                store.CompleteRefreshRun(
                    runId,
                    "failure",
                    DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    ex.Message);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
    }

    private RefreshSourceStatusRow ProcessSourceUnit(
        RefreshSourceInput source,
        IReadOnlyList<CanonicalRecord> existingOtlp,
        HashSet<string> existingOtlpSpanIds,
        string coveredFromUtc,
        string coveredThroughUtc,
        string importedAtUtc)
    {
        SourceCheckpoint? existing = store.GetSourceCheckpoint(source.HarnessId, source.SourceKey);
        CoverageInterval requested = new(coveredFromUtc, coveredThroughUtc);
        CoverageRequest request = new(source.RevisionToken, source.ParserContractVersion);

        if (CheckpointIsReusable(existing, request) && UncoveredIntervals(existing, requested, request).Count == 0)
        {
            return new RefreshSourceStatusRow(source.HarnessId, source.SourceKey, "skipped", 0);
        }

        try
        {
            IReadOnlyList<SourceRecordEnvelope> envelopes = source.LoadEnvelopes();
            IReadOnlyList<CanonicalRecord> synthesized = CanonSynth.SynthesizeEnvelopes(envelopes);
            IReadOnlyList<CanonicalRecord> deduplicated = CanonSynthDedup.Deduplicate(synthesized, existingOtlp, store.RecordProblem);

            List<CanonicalRecord> recordsToCommit = [];
            List<RecordProvenanceRow> provenances = [];

            foreach (CanonicalRecord record in deduplicated)
            {
                bool isSynth = record.SpanId.StartsWith(CanonSynth.SynthSpanPrefix, StringComparison.Ordinal);
                if (!isSynth && existingOtlpSpanIds.Contains(record.SpanId))
                {
                    if (record.Parts is { Count: > 0 })
                    {
                        recordsToCommit.Add(record);
                    }

                    continue;
                }

                recordsToCommit.Add(record);

                if (isSynth)
                {
                    (string? nativeSid, string? nativeRid, string locationToken) = ExtractProvenanceFields(record);
                    provenances.Add(new RecordProvenanceRow(
                        SpanId: record.SpanId,
                        HarnessId: source.HarnessId,
                        SourceKey: source.SourceKey,
                        NativeSessionId: nativeSid,
                        NativeRecordId: nativeRid,
                        SourceRevision: source.RevisionToken,
                        ParserVersion: source.ParserContractVersion,
                        ImportedAtUtc: importedAtUtc,
                        LocationToken: locationToken));
                }
            }

            SourceCheckpoint updatedCheckpoint = new(
                HarnessId: source.HarnessId,
                SourceKey: source.SourceKey,
                ProviderId: source.ProviderId,
                ParserId: source.ParserId,
                ParserContractVersion: source.ParserContractVersion,
                Format: source.Format,
                SourceRootLabel: source.SourceRootLabel,
                RevisionToken: source.RevisionToken,
                CoveredFromUtc: coveredFromUtc,
                CoveredThroughUtc: coveredThroughUtc,
                LastAttemptUtc: importedAtUtc,
                LastSuccessUtc: importedAtUtc,
                LastStatus: "ok",
                LastErrorCode: null,
                UnitCount: envelopes.Count,
                RecordCount: recordsToCommit.Count);

            store.CommitSourceUnit(updatedCheckpoint, recordsToCommit, provenances);
            return new RefreshSourceStatusRow(source.HarnessId, source.SourceKey, "committed", recordsToCommit.Count);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or ArgumentException)
        {
            SourceCheckpoint failedCheckpoint = new(
                HarnessId: source.HarnessId,
                SourceKey: source.SourceKey,
                ProviderId: source.ProviderId,
                ParserId: source.ParserId,
                ParserContractVersion: source.ParserContractVersion,
                Format: source.Format,
                SourceRootLabel: source.SourceRootLabel,
                RevisionToken: source.RevisionToken,
                CoveredFromUtc: existing?.CoveredFromUtc ?? coveredFromUtc,
                CoveredThroughUtc: existing?.CoveredThroughUtc ?? coveredThroughUtc,
                LastAttemptUtc: importedAtUtc,
                LastSuccessUtc: existing?.LastSuccessUtc,
                LastStatus: "failed",
                LastErrorCode: "HARNESS_REFRESH_ERROR",
                UnitCount: 0,
                RecordCount: 0);

            store.UpsertSourceCheckpoint(failedCheckpoint);
            return new RefreshSourceStatusRow(source.HarnessId, source.SourceKey, "failed", 0, ex.Message);
        }
    }

    private static (string? NativeSessionId, string? NativeRecordId, string LocationToken) ExtractProvenanceFields(CanonicalRecord record)
    {
        if (record.Raw is not null
            && record.Raw.TryGetValue("provenance", out JsonElement provEl)
            && provEl.ValueKind == JsonValueKind.Object)
        {
            string? nativeSid = provEl.TryGetProperty("nativeSessionId", out JsonElement sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? sidEl.GetString()
                : record.SessionId;
            string? nativeRid = provEl.TryGetProperty("nativeRecordId", out JsonElement ridEl) && ridEl.ValueKind == JsonValueKind.String
                ? ridEl.GetString()
                : null;
            string loc = provEl.TryGetProperty("locationToken", out JsonElement locEl) && locEl.ValueKind == JsonValueKind.String
                ? locEl.GetString() ?? string.Empty
                : string.Empty;
            return (nativeSid, nativeRid, loc);
        }

        return (record.SessionId, null, string.Empty);
    }

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
