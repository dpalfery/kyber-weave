namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>A deployed path and the exact bytes over which Kyber Utilities has authority.</summary>
/// <remarks>
/// Mirrors <c>SquadOwnedFile</c>, minus Squad's <c>Adopted</c> flag: Kyber Utilities never adopts
/// an existing file, because an unmanaged file at a target path is refused rather than taken over.
/// <see cref="Sha256"/> is the digest of the bytes the deploy wrote, which is what lets a later
/// <c>status</c>, <c>doctor</c>, or <c>remove</c> tell a Kyber-owned file from a locally edited one.
/// </remarks>
public sealed record StatusLineOwnedFile(string RelativePath, string Sha256, StatusLineTarget Target);
