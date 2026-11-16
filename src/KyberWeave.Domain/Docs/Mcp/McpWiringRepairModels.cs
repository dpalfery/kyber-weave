namespace KyberWeave.Core.Docs.Mcp;

/// <summary>What the repair did to one target file.</summary>
public enum McpWiringChangeOutcome
{
    /// <summary>The file was absent and now holds the declared servers' entries.</summary>
    Created,

    /// <summary>Entries were inserted into a file that already existed.</summary>
    Inserted,

    /// <summary>Removal restored the file's pre-write bytes.</summary>
    Restored,

    /// <summary>Removal deleted a file the repair had created.</summary>
    Deleted,

    /// <summary>Nothing was written to the target, and <see cref="McpWiringFileChange.Code"/> says why.</summary>
    Refused
}

/// <summary>What one target of a repair or a removal came to.</summary>
/// <param name="Harness">The harness slug the change is about.</param>
/// <param name="Path">
/// The repository-relative file, or null when the harness has no file in the tree to write.
/// </param>
/// <param name="Outcome">What happened.</param>
/// <param name="Servers">The server names the change concerns.</param>
/// <param name="Code">The stable <c>KW-*</c> rule id, on a refusal.</param>
/// <param name="Message">One sentence describing the change or the refusal.</param>
/// <param name="Hint">The remedy, as the command or edit to make.</param>
/// <param name="Written">Whether bytes reached the target. Always false in a dry run.</param>
/// <param name="Snippet">For a harness the repair does not write: what to paste instead.</param>
public sealed record McpWiringFileChange(
    string Harness,
    string? Path,
    McpWiringChangeOutcome Outcome,
    IReadOnlyList<string> Servers,
    string? Code = null,
    string? Message = null,
    string? Hint = null,
    bool Written = false,
    string? Snippet = null);

/// <summary>Every target's outcome, in the order the targets were requested.</summary>
/// <param name="Files">One change per target.</param>
/// <param name="DryRun">Whether the report describes a run that wrote nothing.</param>
public sealed record McpWiringRepairReport(IReadOnlyList<McpWiringFileChange> Files, bool DryRun);
