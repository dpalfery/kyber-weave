using System.Text.Json;
using System.Text.Json.Nodes;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Applies <see cref="SquadHookJsonBlock"/> edits to hook files on disk.</summary>
public static class SquadHookJsonFile
{
    /// <summary>
    /// Replaces Squad's entries in <paramref name="filePath"/> with <paramref name="preToolUse"/>,
    /// <paramref name="postToolUse"/> and, for Factory, <paramref name="subagentStop"/>, appending
    /// them at the end of each owned container (or setting the Antigravity group key). A missing
    /// or blank file starts from the minimal document. Returns the owned entries written.
    /// </summary>
    public static IReadOnlyList<SquadHookOwnedEntry> SpliceFile(
        SquadHookBlockFormat format,
        string filePath,
        IReadOnlyList<JsonNode> preToolUse,
        IReadOnlyList<JsonNode> postToolUse,
        IReadOnlyList<JsonNode>? subagentStop = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(preToolUse);
        ArgumentNullException.ThrowIfNull(postToolUse);
        SquadHookJsonBlock.RefuseForeignSubagentStop(format, subagentStop);

        string? existing = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
        string content;
        IReadOnlyList<SquadHookOwnedEntry> owned;
        try
        {
            (content, owned) = SquadHookJsonBlock.SpliceContent(format, existing, preToolUse, postToolUse, subagentStop);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Squad hook splice failed: '{filePath}' does not parse as a hook file for {SquadHookJsonBlock.Harness(format)}.",
                ex);
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, content);
        return owned;
    }

    /// <summary>
    /// Removes Squad's entries from <paramref name="filePath"/>, restoring the user's content.
    /// A missing file needs no work; an unparsable file fails and is left untouched.
    /// </summary>
    public static void RemoveFile(SquadHookBlockFormat format, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            return;
        }

        string existing = File.ReadAllText(filePath);
        string content;
        try
        {
            content = SquadHookJsonBlock.RemoveContent(format, existing);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Squad hook removal failed: '{filePath}' does not parse as a hook file for {SquadHookJsonBlock.Harness(format)}.",
                ex);
        }

        File.WriteAllText(filePath, content);
    }
}
