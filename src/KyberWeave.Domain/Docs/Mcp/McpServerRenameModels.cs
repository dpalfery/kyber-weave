namespace KyberWeave.Core.Docs.Mcp;

/// <summary>What one target of a rename came to.</summary>
public enum McpRenameOutcome
{
    /// <summary>The target holds no old key, so there is nothing to rename.</summary>
    NothingToDo,

    /// <summary>A proven old key was rewritten to its shipped name.</summary>
    Renamed,

    /// <summary>A proven old key was removed because the shipped name already held the same launch.</summary>
    RemovedDuplicate,

    /// <summary>An old key was reported and left alone, because it could not be proven ours.</summary>
    Reported,

    /// <summary>The target could not be migrated, and no byte of it was written.</summary>
    Refused
}

/// <summary>What a rename found, did, or refused, in the words the operator reads.</summary>
/// <param name="Harness">The target slug the change is about, or <c>declaration</c> for the host declaration.</param>
/// <param name="Path">The repository-relative file, or null when the target has no file in the tree.</param>
/// <param name="Outcome">What happened, or what would happen in a dry run.</param>
/// <param name="OldKey">The retired key the change is about, when there is one.</param>
/// <param name="NewKey">The shipped key the change is about, when there is one.</param>
/// <param name="Code">The stable <c>KW-*</c> rule id the change carries.</param>
/// <param name="Message">One sentence describing the change or the refusal.</param>
/// <param name="Hint">The remedy, as the command or edit to make.</param>
/// <param name="Snippet">For a harness this tool does not write: what to paste instead.</param>
/// <param name="Written">Whether bytes reached the file. Always false in a dry run.</param>
public sealed record McpRenameChange(
    string Harness,
    string? Path,
    McpRenameOutcome Outcome,
    string? OldKey = null,
    string? NewKey = null,
    string? Code = null,
    string? Message = null,
    string? Hint = null,
    string? Snippet = null,
    bool Written = false);

/// <summary>
/// One retired MCP server key, the key that replaced it, and the launch that proves an entry written
/// under the old one is Kyber-Weave's.
/// </summary>
/// <param name="OldKey">The key existing installs were written under.</param>
/// <param name="NewKey">The key this version writes.</param>
/// <param name="Binary">The file name of the binary the old key's server launches.</param>
/// <param name="NextArgument">
/// The argument that must follow the binary for the launch to be this server's, or null when the
/// binary alone identifies it.
/// </param>
public sealed record McpRenameRule(
    string OldKey,
    string NewKey,
    string Binary,
    string? NextArgument);
