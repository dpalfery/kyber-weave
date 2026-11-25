using KyberWeave.Arbiter.Hooks;

namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// Keeps the background dispatches the Arbiter is following, one record per agent, so the hook process that sees a
/// turn end can find the dispatch a different hook process saw start.
/// </summary>
public interface IBackgroundDispatchStore
{
    /// <summary>Writes the record for its agent, replacing an earlier one.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="record">The record to keep.</param>
    void Save(string arbiterDirectory, BackgroundDispatchRecord record);

    /// <summary>Reads the record for one agent.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="sessionId">The harness session the agent runs in.</param>
    /// <param name="agentId">The harness's id for the agent.</param>
    /// <returns>The record, or null when the agent is not followed.</returns>
    BackgroundDispatchRecord? Find(string arbiterDirectory, string sessionId, string agentId);

    /// <summary>Reads every record of one session.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="sessionId">The harness session.</param>
    IReadOnlyList<BackgroundDispatchRecord> ForSession(string arbiterDirectory, string sessionId);

    /// <summary>
    /// Keeps the report of an agent the Arbiter is not following, so the completed dispatch event that arrives later,
    /// in another hook process, can record it. Replaces a report kept earlier for the same agent.
    /// </summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="sessionId">The harness session the agent runs in.</param>
    /// <param name="agentId">The harness's id for the agent.</param>
    /// <param name="message">The agent's report.</param>
    void Keep(string arbiterDirectory, string sessionId, string agentId, string message);

    /// <summary>Takes the report kept for one agent, removing it, so it is recorded at most once.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="sessionId">The harness session the agent runs in.</param>
    /// <param name="agentId">The harness's id for the agent.</param>
    /// <returns>The kept report, or null when none is kept.</returns>
    string? Take(string arbiterDirectory, string sessionId, string agentId);
}
