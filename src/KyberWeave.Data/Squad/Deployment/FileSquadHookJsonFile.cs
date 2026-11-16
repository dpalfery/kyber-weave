using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Edits hook files on disk through <see cref="SquadHookJsonFile"/>.</summary>
public sealed class FileSquadHookJsonFile : ISquadHookJsonFile
{
    /// <inheritdoc />
    public IReadOnlyList<SquadHookOwnedEntry> SpliceFile(
        SquadHookBlockFormat format,
        string filePath,
        IReadOnlyList<JsonNode> preToolUse,
        IReadOnlyList<JsonNode> postToolUse,
        IReadOnlyList<JsonNode>? subagentStop = null) =>
        SquadHookJsonFile.SpliceFile(format, filePath, preToolUse, postToolUse, subagentStop);

    /// <inheritdoc />
    public void RemoveFile(SquadHookBlockFormat format, string filePath) =>
        SquadHookJsonFile.RemoveFile(format, filePath);
}
