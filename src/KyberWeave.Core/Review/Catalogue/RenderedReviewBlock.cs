namespace KyberWeave.Core.Review.Catalogue;

/// <summary>
/// The <c>review:</c> block rendered from the catalogue, with a structured account of what it wrote.
/// </summary>
/// <param name="Yaml">A complete YAML document whose top-level key is <c>review:</c>.</param>
/// <param name="Written">Each active gate, as the technology it serves and its final id.</param>
/// <param name="CommentedOut">Each gate written commented out, with the reason it is not live.</param>
/// <param name="NoFamily">Declared technologies that have no catalogue family, so no gates ship for them.</param>
public sealed record RenderedReviewBlock(
    string Yaml,
    IReadOnlyList<(string Technology, string GateId)> Written,
    IReadOnlyList<(string GateId, string Reason)> CommentedOut,
    IReadOnlyList<string> NoFamily);
