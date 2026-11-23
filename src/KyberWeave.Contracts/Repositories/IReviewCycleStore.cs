using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads and writes the cycle folders a review keeps under <c>.agent_scratchpad/&lt;task-id&gt;/review</c>.</summary>
public interface IReviewCycleStore
{
    /// <summary>Lists the finished cycles under a review folder, oldest first.</summary>
    /// <param name="reviewFolder"><c>.agent_scratchpad/&lt;task-id&gt;/review</c>.</param>
    IReadOnlyList<ReviewCycleRecord> ReadFinished(string reviewFolder);

    /// <summary>Lists the cycles up to and including <paramref name="priorFolder"/>, oldest first.</summary>
    /// <param name="priorFolder">The newest cycle folder to include.</param>
    IReadOnlyList<ReviewCycleRecord> ReadThrough(string priorFolder);

    /// <summary>
    /// Reads one cycle folder, so a caller can read the cycles one at a time and keep the ones that can be read when
    /// another cannot.
    /// </summary>
    /// <param name="cycleFolder">The <c>cycle-&lt;n&gt;</c> folder.</param>
    /// <returns>The cycle's record, or null when the folder is not a numbered cycle folder.</returns>
    ReviewCycleRecord? ReadCycle(string cycleFolder);

    /// <summary>Records which change a cycle reviewed in <c>cycle.json</c>, replacing an earlier record.</summary>
    /// <param name="cycleFolder">The <c>cycle-&lt;n&gt;</c> folder; created when missing.</param>
    /// <param name="identity">The change being reviewed.</param>
    void WriteIdentity(string cycleFolder, ReviewCycleIdentity identity);

    /// <summary>The change a cycle folder recorded, or null when it recorded none or the record is incomplete.</summary>
    ReviewCycleIdentity? ReadRecordedIdentity(string cycleFolder);

    /// <summary>Whether a cycle folder already records which change it reviewed.</summary>
    bool HasIdentity(string cycleFolder);

    /// <summary>The task folders under a scratchpad that hold a <c>review</c> folder, ordered by name.</summary>
    /// <param name="scratchpad">The <c>.agent_scratchpad</c> folder.</param>
    IReadOnlyList<string> TaskFolders(string scratchpad);
}
