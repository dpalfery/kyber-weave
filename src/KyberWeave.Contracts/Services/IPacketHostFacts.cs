using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>The host repository's facts a worker would otherwise open the root <c>AGENTS.md</c> for, appended to a task packet at print time.</summary>
public interface IPacketHostFacts
{
    /// <summary>Builds the section for one task.</summary>
    /// <param name="repoRoot">The repository root, where the host <c>AGENTS.md</c> is read.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="role">The agent working the task: the worker's own name, else the one the plan assigned; null when neither is known.</param>
    /// <param name="taskId">The task id, for the scratchpad path.</param>
    /// <param name="diagnostics">Receives notes for the log; they are never part of the returned text.</param>
    /// <param name="technology">The one technology the task names, so its gates resolve by technology; null when it names none.</param>
    string Render(
        string repoRoot,
        KyberWeaveConfig config,
        string? role,
        string? taskId,
        IList<string>? diagnostics = null,
        string? technology = null);
}
