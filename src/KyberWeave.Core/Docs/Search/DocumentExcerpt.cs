using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Search;

/// <summary>
/// As much of one document as the caller's budget allows, most relevant sections first
/// chosen but emitted in document order.
/// </summary>
/// <param name="Sections">The included sections, in the order they appear in the file.</param>
/// <param name="OmittedHeadings">
/// Headings that did not fit. Naming them is the point: the caller learns what else the
/// document holds without having to open it, and can ask for more deliberately.
/// </param>
/// <param name="IsComplete">True when nothing was omitted — the whole document is here.</param>
/// <param name="BudgetExhausted">
/// True when at least one section was dropped for lack of budget rather than for lack of
/// relevance. The distinction decides what to tell the caller: only here is asking again
/// with a larger budget worth doing.
/// </param>
public sealed record DocumentExcerpt(
    IReadOnlyList<DocumentSection> Sections,
    IReadOnlyList<string> OmittedHeadings,
    bool IsComplete,
    bool BudgetExhausted)
{
    public static readonly DocumentExcerpt Empty = new([], [], false, false);
}
