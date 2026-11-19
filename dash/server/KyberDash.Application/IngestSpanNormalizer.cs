using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Turns decoded OTLP wire spans into the canonical records and relational collector batches
/// the ingest pipeline would have accepted, without persisting anything.
/// </summary>
/// <remarks>
/// Delegates directly to <see cref="CanonIngestPipeline.NormalizeBatch"/> so normalization runs
/// purely in memory with no throwaway repository sink and identical ID, quarantine, and accounting
/// rules.
/// </remarks>
internal static class IngestSpanNormalizer
{
    /// <summary>
    /// Normalizes <paramref name="spans"/> into relational DTO batches, quarantined spans, problems,
    /// and canonical records.
    /// </summary>
    /// <param name="spans">Decoded OTLP spans to normalize.</param>
    /// <returns>Normalized relational batches plus legacy DTO views and outcome counters.</returns>
    public static NormalizedSpanBatch Normalize(IReadOnlyList<OtlpSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        return CanonIngestPipeline.NormalizeBatch(spans);
    }

    /// <summary>A normalization pass's accepted, quarantined, and rejected material.</summary>
    internal class NormalizedSpanBatch
    {
        /// <summary>An empty pass.</summary>
        public static NormalizedSpanBatch Empty { get; } = new(
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            new IngestOutcome(0, 0, 0));

        /// <summary>Initializes a batch from one normalization pass.</summary>
        /// <param name="records">Accepted canonical records, in input order.</param>
        /// <param name="quarantined">Legacy quarantined span rows.</param>
        /// <param name="problems">Legacy problem rows for rejected spans.</param>
        /// <param name="relationalBatches">Relational collector batches grouped by harness and source.</param>
        /// <param name="quarantinedSpans">Operational quarantined span entries without raw payloads.</param>
        /// <param name="relationalProblems">All relational ingest problems recorded for rejected spans.</param>
        /// <param name="standaloneProblems">Relational problems not attached to an accepted batch.</param>
        /// <param name="outcome">Per-batch accepted, quarantined, and rejected counters.</param>
        public NormalizedSpanBatch(
            IReadOnlyList<CanonicalRecord> records,
            IReadOnlyList<QuarantineRow> quarantined,
            IReadOnlyList<SpanProblemRow> problems,
            IReadOnlyList<CollectedBatch> relationalBatches,
            IReadOnlyList<StoredQuarantinedSpan> quarantinedSpans,
            IReadOnlyList<CollectedProblem> relationalProblems,
            IReadOnlyList<CollectedProblem> standaloneProblems,
            IngestOutcome outcome)
        {
            Records = records;
            Quarantined = quarantined;
            Problems = problems;
            RelationalBatches = relationalBatches;
            QuarantinedSpans = quarantinedSpans;
            RelationalProblems = relationalProblems;
            StandaloneProblems = standaloneProblems;
            Outcome = outcome;
        }

        /// <summary>Accepted canonical records, in input order.</summary>
        public IReadOnlyList<CanonicalRecord> Records { get; }

        /// <summary>Spans the pipeline would have quarantined (legacy DTO view).</summary>
        public IReadOnlyList<QuarantineRow> Quarantined { get; }

        /// <summary>Problems the pipeline would have recorded for rejected spans (legacy DTO view).</summary>
        public IReadOnlyList<SpanProblemRow> Problems { get; }

        /// <summary>Normalized relational collector batches grouped by harness and source.</summary>
        public IReadOnlyList<CollectedBatch> RelationalBatches { get; }

        /// <summary>Normalized quarantined spans for <see cref="IOperationalStore"/> without raw payloads.</summary>
        public IReadOnlyList<StoredQuarantinedSpan> QuarantinedSpans { get; }

        /// <summary>All normalized ingest problems produced during the pass.</summary>
        public IReadOnlyList<CollectedProblem> RelationalProblems { get; }

        /// <summary>Normalized ingest problems not already carried by <see cref="RelationalBatches"/>.</summary>
        public IReadOnlyList<CollectedProblem> StandaloneProblems { get; }

        /// <summary>Per-batch accepted, quarantined, and rejected counters.</summary>
        public IngestOutcome Outcome { get; }

        /// <summary>Whether the pass carried no telemetry at all.</summary>
        public bool IsEmpty => Records.Count == 0 && Quarantined.Count == 0 && Problems.Count == 0;
    }
}
