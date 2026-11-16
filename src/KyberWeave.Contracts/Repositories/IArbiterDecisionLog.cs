using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The Arbiter's decision log: every decision it records, and the repeat count the escalation envelope reads.</summary>
public interface IArbiterDecisionLog
{
    /// <summary>The directory holding the ledger, the decision log and the lock.</summary>
    string DirectoryPath { get; }

    /// <summary>Appends one decision as one complete line under the exclusive lock.</summary>
    /// <param name="record">The decision.</param>
    /// <param name="cancellationToken">Cancels the append.</param>
    /// <exception cref="IOException">The lock is not acquired in time (fail closed).</exception>
    Task AppendAsync(ArbiterDecisionRecord record, CancellationToken cancellationToken = default);

    /// <summary>Reads every complete decision in the newest window of the file, in append order. Takes no lock.</summary>
    IReadOnlyList<ArbiterDecisionRecord> ReadAll();

    /// <summary>Reads like <see cref="ReadAll"/> and reports what the read could not show.</summary>
    ArbiterLogRead<ArbiterDecisionRecord> ReadAllWithDiagnostics();

    /// <summary>The number of times a rule has escalated the same task, counting the current one.</summary>
    /// <param name="ruleId">The rule that escalated.</param>
    /// <param name="planFile">The plan file the dispatch named, or null.</param>
    /// <param name="planDigest">The plan file's digest, or null.</param>
    /// <param name="task">The task the dispatch named, or null.</param>
    /// <param name="session">The session, consulted only when there is no plan file.</param>
    int Repeat(string ruleId, string? planFile, string? planDigest, string? task, string? session = null);
}
