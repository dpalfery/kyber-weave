

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the Claude hook sources a repository and its user declare.</summary>
public interface IClaudeHookCoverage
{
    /// <summary>
    /// The sources that exist for a payload, before any of them is read for its hooks. Use
    /// <paramref name="agentType"/> null to list the project's own sources, as the doctor does.
    /// </summary>
    /// <param name="projectRoot">The repository root that holds <c>.claude/</c>.</param>
    /// <param name="homeDirectory">The user's home directory, whose <c>.claude/</c> holds the personal agents and skills.</param>
    /// <param name="agentType">The payload's agent type, or null for a main-thread dispatch.</param>
    /// <returns>
    /// The relative path of each project source (for example <c>.claude/settings.json</c>), and the full
    /// path of each personal source, which lives outside the project.
    /// </returns>
    IReadOnlyList<string> EffectiveSources(string projectRoot, string homeDirectory, string? agentType);

    /// <summary>
    /// The sources whose hooks would fire for one tool call. A source counts only when its hooks map has
    /// the exact container named by <paramref name="eventName"/> holding an entry whose matcher matches
    /// <paramref name="toolName"/> and whose command runs the project-scope Arbiter hook.
    /// </summary>
    /// <param name="projectRoot">The repository root that holds <c>.claude/</c>.</param>
    /// <param name="homeDirectory">The user's home directory, whose <c>.claude/</c> holds the personal agents and skills.</param>
    /// <param name="agentType">The payload's agent type, or null for a main-thread dispatch.</param>
    /// <param name="eventName"><c>PreToolUse</c> or <c>PostToolUse</c>.</param>
    /// <param name="toolName">The tool the event is for, matched against each entry's matcher.</param>
    /// <returns>The display path of each source that would fire, in a stable order.</returns>
    IReadOnlyList<string> CandidateSources(
        string projectRoot,
        string homeDirectory,
        string? agentType,
        string eventName,
        string toolName);

    /// <summary>
    /// The project's own sources that would fire for one tool call under any agent type: the settings
    /// files, the effective skills, and each project agent file. The settings files and skills are read
    /// once, however many agent files there are.
    /// </summary>
    /// <param name="projectRoot">The repository root that holds <c>.claude/</c>.</param>
    /// <param name="homeDirectory">The user's home directory, whose <c>.claude/</c> holds the personal skills.</param>
    /// <param name="eventName"><c>PreToolUse</c> or <c>PostToolUse</c>.</param>
    /// <param name="toolName">The tool the event is for, matched against each entry's matcher.</param>
    /// <returns>The display path of each source that would fire, in a stable order.</returns>
    IReadOnlyList<string> ProjectCandidateSources(
        string projectRoot,
        string homeDirectory,
        string eventName,
        string toolName);
}
