namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>The ownership boundary for one status-line deployment.</summary>
/// <remarks>
/// Deliberately separate from <c>SquadReceipt</c> (D3): Squad's receipt carries a lock schema,
/// deployment scope, and root layout that a status-line deployment has no use for, and D3 keeps
/// the two ownership records from sharing a format so neither can be misread as the other.
///
/// <para>
/// An empty <see cref="Files"/> list is a real value, not an absent one: it is what a completed
/// <c>remove</c> leaves behind. The persisted receipt file is deleted when nothing is owned rather
/// than written empty, so an empty receipt in memory never has a stale file beside it.
/// </para>
/// </remarks>
public sealed record StatusLineReceipt(IReadOnlyList<StatusLineOwnedFile> Files);
