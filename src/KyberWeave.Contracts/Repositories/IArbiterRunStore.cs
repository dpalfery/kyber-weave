using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Stores;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Holds the text of review findings, worker failures and worker return tails outside the decision log and the ledger.</summary>
public interface IArbiterRunStore
{
    /// <summary>Appends one finding's text to the store of a review run.</summary>
    /// <param name="reviewRun">The <c>REVIEW_RUN</c> id the finding belongs to.</param>
    /// <param name="text">The finding's text, stored as given.</param>
    /// <param name="ct">Cancels the append.</param>
    Task<ArbiterStoreStatus> AppendFindingAsync(string reviewRun, string text, CancellationToken ct = default);

    /// <summary>Appends one refuter's verdict on a finding to the store of a review run.</summary>
    /// <param name="reviewRun">The <c>REVIEW_RUN</c> id the finding belongs to.</param>
    /// <param name="findingSlug">The slug of the finding the verdict is on.</param>
    /// <param name="verdict">The refuter's verdict.</param>
    /// <param name="ct">Cancels the append.</param>
    Task<ArbiterStoreStatus> AppendRefutationOutcomeAsync(string reviewRun, string findingSlug, RefutationVerdict verdict, CancellationToken ct = default);

    /// <summary>Reads the findings stored for a review run, oldest first.</summary>
    /// <param name="reviewRun">The <c>REVIEW_RUN</c> id to read.</param>
    IReadOnlyList<string> ReadFindings(string reviewRun);

    /// <summary>Reads the failures stored for a plan or session, oldest first.</summary>
    /// <param name="planFile">The plan file the failures belong to, or null.</param>
    /// <param name="planDigest">The digest of that plan file, or null.</param>
    /// <param name="session">The session, used as the key when there is no plan.</param>
    IReadOnlyList<string> ReadFailures(string? planFile, string? planDigest, string? session);

    /// <summary>Appends one failure's text to the failure store.</summary>
    /// <param name="planFile">The plan file the failure belongs to, or null.</param>
    /// <param name="planDigest">The digest of that plan file, or null.</param>
    /// <param name="session">The session, used as the key when there is no plan.</param>
    /// <param name="text">The failure line.</param>
    /// <param name="ct">Cancels the append.</param>
    Task<ArbiterStoreStatus> AppendFailureAsync(string? planFile, string? planDigest, string? session, string text, CancellationToken ct = default);

    /// <summary>Appends the tail of one worker's return to the store of its plan.</summary>
    /// <param name="planFile">The plan file the return's dispatch named.</param>
    /// <param name="text">The stored text.</param>
    /// <param name="ct">Cancels the append.</param>
    Task<ArbiterStoreStatus> AppendReturnAsync(string planFile, string text, CancellationToken ct = default);

    /// <summary>Reads the return tails stored for a plan, oldest first.</summary>
    /// <param name="planFile">The plan file the returns belong to.</param>
    IReadOnlyList<string> ReadReturns(string planFile);
}
