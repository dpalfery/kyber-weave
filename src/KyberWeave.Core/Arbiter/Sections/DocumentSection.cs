namespace KyberWeave.Core.Arbiter.Sections;

/// <summary>One section of a markdown document: a heading and the text under it, up to the next heading.</summary>
/// <param name="Id">The heading slug path, for example <c>guide/install</c>; the front matter section is <see cref="DocumentSectionIndex.FrontMatterId"/>.</param>
/// <param name="Title">The heading text, or <c>Front matter</c> for the front matter section.</param>
/// <param name="StartLine">The 1-based line the section starts on.</param>
/// <param name="EndLine">The 1-based line the section ends on, inclusive.</param>
/// <param name="Text">The section's lines joined with newlines, cut to <see cref="DocumentSectionIndex.MaxSectionCharacters"/>.</param>
/// <param name="Truncated">True when the text was cut short, or the scan stopped before the section's end.</param>
public sealed record DocumentSection(string Id, string Title, int StartLine, int EndLine, string Text, bool Truncated = false);
