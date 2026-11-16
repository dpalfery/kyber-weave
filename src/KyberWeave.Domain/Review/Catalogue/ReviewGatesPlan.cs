using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// The rendered block <c>docs init</c> may write, and whether it may write it at all.
/// </summary>
/// <param name="Rendered">The catalogue render for the declared technologies.</param>
/// <param name="ShouldWrite">
/// False when no declared technology has a catalogue entry; there is then no block to write.
/// </param>
public sealed record ReviewGatesPlan(RenderedReviewBlock Rendered, bool ShouldWrite);
