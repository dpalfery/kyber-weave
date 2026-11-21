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
    /// Persists a single collector diagnostic problem into <c>ingest_problem</c> and its
    /// optional child tables.
    /// </summary>
    /// <param name="problem">Diagnostic problem to persist.</param>
    void WriteProblem(CollectedProblem problem);
}
