using System.Globalization;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Canonical span normalization and local transcript ingestion backed by
/// <see cref="CanonIngestPipeline"/> and <see cref="CanonRefreshEngine"/>.
/// </summary>
/// <remarks>
/// Every write path into <c>records</c> goes through this service: OTLP admission, transcript
/// refresh, and deterministic seeding. Attribution, token validation, quarantine, cross-path
/// deduplication, and retention purging all live here so a single ingestion dependency covers them.
/// </remarks>
public class TranscriptIngestionService : ITranscriptIngestionService
{
    private readonly ICanonStoreRepository store;
    private readonly ITranscriptReaderRepository transcripts;
    private readonly CanonRefreshEngine refresh;

    /// <summary>
    /// Initializes a new <see cref="TranscriptIngestionService"/>.
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

        this.store = store;
        this.transcripts = transcripts;
        this.refresh = refresh;
    }

    /// <inheritdoc />
    public IngestOutcome IngestSpans(IReadOnlyList<OtlpSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        return spans.Count == 0 ? new IngestOutcome(0, 0, 0) : CanonIngestPipeline.IngestBatch(spans, store);
    }

    /// <inheritdoc />
    public RefreshReport RefreshHarnessSources(int historyWeeks = 2)
    {
        int weeks = CanonSchema.NormalizeHistoryWeeks(historyWeeks) ?? CanonRefreshEngine.DefaultHistoryWeeks;
        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-7.0 * weeks);
        IReadOnlyList<RefreshSourceInput> sources = transcripts.DiscoverLocalSources(since);
        return refresh.RefreshHarnessSources(sources, weeks, trigger: "refresh");
    }

    /// <inheritdoc />
    public PurgeExpiredContentResult PurgeExpiredContent(DateTimeOffset? now = null) =>
        refresh.PurgeExpiredContent(now);

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
        return refresh.RefreshHarnessSources(sources, historyWeeks, trigger);
    }

    /// <summary>Retention floor applied by <see cref="PurgeExpiredContent"/>.</summary>
    /// <param name="now">Reference instant.</param>
    /// <returns>The instant before which record content is cleared.</returns>
    public static DateTimeOffset RetentionFloor(DateTimeOffset now) =>
        CanonRefreshEngine.ContentRetentionFloor(now);

    /// <summary>The retention floor for the current instant, formatted as the purge cutoff.</summary>
    /// <returns>ISO-8601 UTC cutoff.</returns>
    public static string RetentionFloorIso() =>
        CanonRefreshEngine.ContentRetentionFloor(DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
