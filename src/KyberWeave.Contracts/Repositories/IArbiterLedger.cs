using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The Arbiter's in-flight ledger: append-only pre and post dispatch events and the questions the rules put to them.</summary>
public interface IArbiterLedger
{
    /// <summary>The directory holding the ledger, the decision log and the lock.</summary>
    string DirectoryPath { get; }

    /// <summary>Appends one event as one complete line under the exclusive lock.</summary>
    /// <param name="record">The event; a post event without a pre id is paired first.</param>
    /// <param name="cancellationToken">Cancels the append.</param>
    /// <exception cref="IOException">The lock is not acquired in time (fail closed).</exception>
    Task AppendAsync(ArbiterLedgerEvent record, CancellationToken cancellationToken = default);

    /// <summary>Reads every complete event in the newest window of the file, in append order. Takes no lock.</summary>
    IReadOnlyList<ArbiterLedgerEvent> ReadAll();

    /// <summary>Reads like <see cref="ReadAll"/> and reports what the read could not show.</summary>
    ArbiterLogRead<ArbiterLedgerEvent> ReadAllWithDiagnostics();

    /// <summary>Resolves the pre event a post event pairs with.</summary>
    /// <param name="post">The return.</param>
    /// <returns>The pre event id, or null when none pairs.</returns>
    string? MatchPre(ArbiterLedgerEvent post);

    /// <summary>The task files of the other unpaired <c>delegate</c> pre events.</summary>
    /// <param name="excludeEventId">The pre event being gated now, if it is already recorded.</param>
    /// <param name="session">When set, only dispatches from this session count.</param>
    /// <param name="notDispatched">Pre events that were refused, so never ran.</param>
    /// <param name="run">When set, only dispatches of this run count.</param>
    IReadOnlyList<string> InFlightPaths(
        string? excludeEventId = null,
        string? session = null,
        IReadOnlySet<string>? notDispatched = null,
        ArbiterRunScope? run = null);

    /// <summary>The writer dispatches in flight at any moment between a dispatch and its return.</summary>
    /// <param name="preId">The pre event of the dispatch that returned, or null when the return paired with none.</param>
    /// <param name="postId">Its return.</param>
    /// <param name="notDispatched">Pre events that were refused, so never ran.</param>
    ConcurrentDispatches? ConcurrentWriters(string? preId, string postId, IReadOnlySet<string>? notDispatched = null);

    /// <summary>The headers of the dispatch a return answers.</summary>
    /// <param name="postId">The return's event id.</param>
    ArbiterLedgerHeaders? DispatchHeadersFor(string postId);

    /// <summary>The tasks whose return passed review or attested completion.</summary>
    /// <param name="planFile">When set, only returns for this plan count.</param>
    /// <param name="run">When set, only returns of this run's dispatches count.</param>
    IReadOnlyList<string> CompletedTasks(string? planFile = null, ArbiterRunScope? run = null);
}
