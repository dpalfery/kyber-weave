namespace KyberWeave.Core.Review;

/// <summary>One member of a duplicate cluster.</summary>
/// <param name="Name">Bare symbol name. Two members of a cluster need not share it.</param>
/// <param name="QualifiedName">Fully qualified name, when the index recorded one.</param>
/// <param name="File">Repository-relative path.</param>
/// <param name="StartLine">1-based first line of the declaration.</param>
/// <param name="EndLine">1-based last line of the declaration.</param>
public sealed record DuplicateMember(
    string Name,
    string QualifiedName,
    string File,
    int StartLine,
    int EndLine);

/// <summary>A set of symbols whose normalized bodies are identical.</summary>
/// <param name="Id">Stable identifier, derived from the body hash rather than from ordering.</param>
/// <param name="NormalizedLines">How many lines the shared body is, after normalization.</param>
/// <param name="Members">Every symbol carrying that body, ordered by file then line.</param>
public sealed record DuplicateCluster(
    string Id,
    int NormalizedLines,
    IReadOnlyList<DuplicateMember> Members);

/// <summary>
/// The duplicates gate's result document.
/// </summary>
/// <param name="Schema">Format identifier, for readers that must tolerate later versions.</param>
/// <param name="IndexPath">Where the CodeGraph index was looked for.</param>
/// <param name="IndexAvailable">Whether it was found and read.</param>
/// <param name="IndexModifiedUtc">
/// When the index file was last written. The index syncs behind the working tree, so a
/// consumer weighing a cluster against uncommitted edits needs to know how stale it is.
/// </param>
/// <param name="UnavailableReason">Why the index could not be read, when it could not.</param>
/// <param name="MinimumLines">The threshold this run applied.</param>
/// <param name="SymbolsConsidered">Bodies that met the threshold and were hashed.</param>
/// <param name="SymbolsUnreadable">
/// Bodies skipped because the index named a file or span the working tree no longer has.
/// Reported rather than swallowed: a high count means the index is stale, and every cluster
/// in the same run is suspect.
/// </param>
/// <param name="Clusters">Duplicate clusters, largest body first.</param>
public sealed record DuplicateReport(
    string Schema,
    string IndexPath,
    bool IndexAvailable,
    DateTime? IndexModifiedUtc,
    string? UnavailableReason,
    int MinimumLines,
    int SymbolsConsidered,
    int SymbolsUnreadable,
    IReadOnlyList<DuplicateCluster> Clusters)
{
    /// <summary>The format identifier every report this build writes carries.</summary>
    public const string CurrentSchema = "kyber-weave.review-duplicates/v1";
}
