using KyberWeave.Core.Arbiter.Sections;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.11 (section trimming): <c>DocumentSectionIndex.Split</c> cuts a markdown document into
/// sections at its headings. Each section carries a heading slug path as its id, its title, its 1-based line range
/// and its text. The split is deterministic and bounded, so a large document cannot produce an unbounded candidate
/// list for the provider.
/// RED: <c>KyberWeave.Core.Arbiter.Sections.DocumentSectionIndex</c> and <c>DocumentSection</c> do not exist yet.
/// </summary>
/// <remarks>
/// Names (GREEN must match): <c>DocumentSection(string Id, string Title, int StartLine, int EndLine, string Text)</c>;
/// <c>DocumentSectionIndex.Split(string markdown)</c>; <c>DocumentSectionIndex.FrontMatterId</c> is the id of the
/// section made of the front matter block and any text before the first heading;
/// <c>DocumentSectionIndex.MaxSections</c> caps the count, and the last section then runs to the end of the document.
/// </remarks>
public sealed class DocumentSectionIndexTests
{
    /// <summary>Headings split the document; a child's id is its parents' slugs joined with '/'.</summary>
    [Fact]
    public void HeadingsSplitTheDocumentIntoSectionsWithSlugPathIds()
    {
        const string markdown = """
            # Guide
            Intro.
            ## Install
            Steps.
            ### Linux
            Apt.
            ## Usage
            Run it.
            """;

        IReadOnlyList<DocumentSection> sections = DocumentSectionIndex.Split(markdown);

        (string Id, string Title, int StartLine, int EndLine)[] expected =
        [
            ("guide", "Guide", 1, 2),
            ("guide/install", "Install", 3, 4),
            ("guide/install/linux", "Linux", 5, 6),
            ("guide/usage", "Usage", 7, 8),
        ];
        Assert.Equal(expected, sections.Select(section => (section.Id, section.Title, section.StartLine, section.EndLine)));
        Assert.Equal("## Install\nSteps.", sections[1].Text);
    }

    /// <summary>
    /// The front matter and any text before the first heading are the title section the selector always keeps.
    /// Failure this guards: a trimmed context that loses the document's own identity and metadata.
    /// </summary>
    [Fact]
    public void FrontMatterFormsTheFrontMatterSectionBeforeTheFirstHeading()
    {
        const string markdown = "---\ntitle: Guide\n---\n# Guide\nBody.\n";

        IReadOnlyList<DocumentSection> sections = DocumentSectionIndex.Split(markdown);

        DocumentSection front = sections[0];
        Assert.Equal(DocumentSectionIndex.FrontMatterId, front.Id);
        Assert.Equal(1, front.StartLine);
        Assert.Equal(3, front.EndLine);
        Assert.Equal("guide", sections[1].Id);
        Assert.Equal(4, sections[1].StartLine);
    }

    /// <summary>
    /// A line that starts with '#' inside a fenced code block is code, not a heading. Failure this guards: a shell
    /// comment or a C# region in an example splits the document into fake sections.
    /// </summary>
    [Fact]
    public void HeadingsInsideCodeFencesAreNotSections()
    {
        const string markdown = "# Real\n```bash\n# not a heading\n```\n";

        IReadOnlyList<DocumentSection> sections = DocumentSectionIndex.Split(markdown);

        DocumentSection only = Assert.Single(sections);
        Assert.Equal("real", only.Id);
        Assert.Equal(4, only.EndLine);
    }

    /// <summary>Two headings with the same title under different parents keep distinct ids, so a section can be cited by id.</summary>
    [Fact]
    public void RepeatedTitlesUnderDifferentParentsKeepDistinctIds()
    {
        const string markdown = "# A\n## Notes\n# B\n## Notes\n";

        IReadOnlyList<DocumentSection> sections = DocumentSectionIndex.Split(markdown);

        Assert.Equal(["a", "a/notes", "b", "b/notes"], sections.Select(section => section.Id));
        Assert.Equal(sections.Count, sections.Select(section => section.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Splitting the same text twice gives the same sections, so the candidate list is stable across runs.</summary>
    [Fact]
    public void SplittingTheSameTextTwiceGivesTheSameSections()
    {
        const string markdown = "# A\nx\n## B\ny\n## C\nz\n";

        IReadOnlyList<DocumentSection> first = DocumentSectionIndex.Split(markdown);
        IReadOnlyList<DocumentSection> second = DocumentSectionIndex.Split(markdown);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// A document with more headings than the bound yields at most <see cref="DocumentSectionIndex.MaxSections"/>
    /// sections, and the last one runs to the end of the document so no text is silently dropped.
    /// </summary>
    [Fact]
    public void ADocumentWithTooManyHeadingsStaysWithinTheSectionBound()
    {
        int headings = DocumentSectionIndex.MaxSections + 300;
        string markdown = string.Join("\n", Enumerable.Range(1, headings).Select(i => $"## Item {i}"));

        IReadOnlyList<DocumentSection> sections = DocumentSectionIndex.Split(markdown);

        Assert.Equal(DocumentSectionIndex.MaxSections, sections.Count);
        Assert.Equal(headings, sections[^1].EndLine);
        Assert.Contains($"## Item {headings}", sections[^1].Text, StringComparison.Ordinal);
    }
}
