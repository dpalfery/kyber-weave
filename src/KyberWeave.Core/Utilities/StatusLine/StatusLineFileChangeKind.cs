namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>Whether a planned status-line change writes new bytes or removes a deployed file.</summary>
/// <remarks>
/// Mirrors <c>SquadFileMutationKind</c>. The vocabulary stays this small on purpose: Kyber
/// Utilities never edits a file it did not write, so there is no update or merge member to add.
/// </remarks>
public enum StatusLineFileChangeKind
{
    /// <summary>Write the artifact's bytes to a path beneath the staging root.</summary>
    Write,

    /// <summary>Remove a receipt-owned file whose bytes are still the ones Kyber deployed.</summary>
    Delete
}
