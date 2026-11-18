namespace KyberWeave.Core.Arbiter.Sections;

/// <summary>One section of a markdown document: a heading and the text under it, up to the next heading.</summary>
/// <param name="Id">The heading slug path, for example <c>guide/install</c>; the front matter section is <c>DocumentSectionIndex.FrontMatterId</c>.</param>
/// <param name="Title">The heading text, or <c>Front matter</c> for the front matter section.</param>
/// <param name="StartLine">The 1-based line the section starts on.</param>
/// <param name="EndLine">The 1-based line the section ends on, inclusive.</param>
/// <param name="Text">The section's lines joined with newlines, cut to <c>DocumentSectionIndex.MaxSectionCharacters</c>.</param>
/// <param name="Truncated">True when the text was cut short, or the scan stopped before the section's end.</param>
public sealed record DocumentSection(string Id, string Title, int StartLine, int EndLine, string Text, bool Truncated = false)
{
    /// <summary>The largest document the section selector reads, in bytes. A larger one fails safe rather than being read whole.</summary>
    public const long MaxDocumentBytes = 1024 * 1024;
}
