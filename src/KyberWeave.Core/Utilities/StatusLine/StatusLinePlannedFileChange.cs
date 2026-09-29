namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>One planned status-line file change, without its payload.</summary>
/// <remarks>
/// The public view of a plan, so a host can report what a deploy or a remove will do — and, for a
/// dry run, prove that no harness settings path is among the changes — without holding the bytes.
/// </remarks>
public sealed record StatusLinePlannedFileChange(string RelativePath, StatusLineFileChangeKind Kind);
