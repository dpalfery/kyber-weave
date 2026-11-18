using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Applies <see cref="SquadHookJsonBlock"/> edits to hook files.</summary>
/// <remarks>
/// The hook file is the user's own, and a harness may reach it through a symbolic link, so it is rewritten in
/// place with a plain overwrite as before; an atomic rename would replace the link with a regular file.
/// </remarks>
/// <param name="files">Reads and rewrites the hook files.</param>
/// <param name="environment">Supplies the base directory a relative hook file path resolves against.</param>
public sealed class SquadHookJsonFile(
    IWritableFileSystem files,
    IProcessEnvironment environment) : ISquadHookJsonFile
{
    /// <summary>
    /// Replaces Squad's entries in <paramref name="filePath"/> with <paramref name="preToolUse"/>,
    /// <paramref name="postToolUse"/> and, for Factory, <paramref name="subagentStop"/>, appending
    /// them at the end of each owned container (or setting the Antigravity group key). A missing
    /// or blank file starts from the minimal document. Returns the owned entries written.
    /// </summary>
    public IReadOnlyList<SquadHookOwnedEntry> SpliceFile(
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

        string fullPath = FullPath(filePath);
        string? existing = files.FileExists(fullPath) ? files.ReadAllText(fullPath) : null;
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

        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            files.CreateDirectory(directory);
        }

        files.WriteAllText(fullPath, content);
        return owned;
    }

    /// <summary>
    /// Removes Squad's entries from <paramref name="filePath"/>, restoring the user's content.
    /// A missing file needs no work; an unparsable file fails and is left untouched.
    /// </summary>
    public void RemoveFile(SquadHookBlockFormat format, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = FullPath(filePath);
        if (!files.FileExists(fullPath))
        {
            return;
        }

        string existing = files.ReadAllText(fullPath);
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

        files.WriteAllText(fullPath, content);
    }

    /// <summary>
    ///     Returns the path as given when it is rooted, otherwise joined onto the working directory. The spelling is
    ///     kept as is, because a decomposed Unicode folder name must still name the folder that holds the file.
    /// </summary>
    private string FullPath(string filePath) =>
        Path.IsPathRooted(filePath) ? filePath : Path.Combine(environment.CurrentDirectory, filePath);
}
