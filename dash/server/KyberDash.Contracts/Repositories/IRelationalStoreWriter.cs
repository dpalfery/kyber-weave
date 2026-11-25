using KyberDash.Data.Collector;

namespace KyberDash.Contracts.Repositories;

/// <summary>
/// Typed relational ingestion writer persisting static collector batches, source deletions,
/// and ingest diagnostic problems into the normalized KyberDash schema.
/// </summary>
public interface IRelationalStoreWriter
{
    /// <summary>
    /// Persists a static collector <paramref name="batch"/> (its <c>source</c>, <c>session</c>,
    /// <c>record</c>, and <c>ingest_problem</c> rows and child extension tables) atomically.
    /// </summary>
    /// <param name="batch">Static collector batch to persist.</param>
    void WriteBatch(CollectedBatch batch);

    /// <summary>
    /// Deletes the <c>source</c> identified by <paramref name="sourceId"/>, cascading via
    /// <c>ON DELETE CASCADE</c> to its owned records and child tables.
    /// </summary>
    /// <param name="sourceId">Primary key of the source row to remove.</param>
    void DeleteSource(string sourceId);

    /// <summary>
    /// Deletes the record token values that <paramref name="estimated"/> names as estimates from the
    /// records of the file source <paramref name="sourceLocator"/> of <paramref name="harnessId"/>, so
    /// they read as not measured. Sessions, records, content and every other row stay.
    /// </summary>
    /// <param name="harnessId">Harness owning the source.</param>
    /// <param name="sourceLocator">File source locator, the source unit's key.</param>
    /// <param name="estimated">Which token values of the source were stored as estimates.</param>
    void ClearEstimatedRecordTokens(string harnessId, string sourceLocator, EstimatedRecordTokens estimated);

    /// <summary>
    /// Persists a single collector diagnostic problem into <c>ingest_problem</c> and its
    /// optional child tables.
    /// </summary>
    /// <param name="problem">Diagnostic problem to persist.</param>
    void WriteProblem(CollectedProblem problem);

    /// <summary>
    /// Deletes the <c>ingest_problem</c> row keyed <paramref name="problemKey"/> and its child rows, if
    /// any; a missing key changes nothing.
    /// </summary>
    /// <param name="problemKey">Key of the problem to remove.</param>
    void DeleteProblem(string problemKey);
}
