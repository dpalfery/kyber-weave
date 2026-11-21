using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Applies Squad's hook block edits to hook files on disk.</summary>
public interface ISquadHookJsonFile
{
    /// <summary>Replaces Squad's entries in a hook file, starting from the minimal document when it is missing or blank.</summary>
    /// <param name="format">The harness's hook file format.</param>
    /// <param name="filePath">The hook file.</param>
    /// <param name="preToolUse">The pre-tool-use entries.</param>
    /// <param name="postToolUse">The post-tool-use entries.</param>
    /// <param name="subagentStop">The subagent-stop entries, for Factory.</param>
    /// <param name="sessionStart">The session-start entries, for Factory's worker guard.</param>
    /// <param name="sessionEnd">The session-end entries, for Factory's worker guard.</param>
    /// <returns>The owned entries written.</returns>
    IReadOnlyList<SquadHookOwnedEntry> SpliceFile(
        SquadHookBlockFormat format,
        string filePath,
        IReadOnlyList<JsonNode> preToolUse,
        IReadOnlyList<JsonNode> postToolUse,
        IReadOnlyList<JsonNode>? subagentStop = null,
        IReadOnlyList<JsonNode>? sessionStart = null,
        IReadOnlyList<JsonNode>? sessionEnd = null);

    /// <summary>Removes Squad's entries from a hook file, restoring the user's content. A missing file needs no work.</summary>
    /// <param name="format">The harness's hook file format.</param>
    /// <param name="filePath">The hook file.</param>
    /// <exception cref="InvalidOperationException">The file does not parse; it is left untouched.</exception>
    void RemoveFile(SquadHookBlockFormat format, string filePath);
}
