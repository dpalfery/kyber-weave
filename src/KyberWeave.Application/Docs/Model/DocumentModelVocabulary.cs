namespace KyberWeave.Core.Docs.Model;

/// <summary>The code references and keywords a document declares, in its frontmatter and in its sections.</summary>
public static class DocumentModelVocabulary
{
    /// <summary>The frontmatter's code references followed by every section's.</summary>
    public static IReadOnlyList<string> CodeRefs(this DocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<string> items = new();
        if (document.Frontmatter.CodeRefs is not null)
        {
            items.AddRange(document.Frontmatter.CodeRefs);
        }

        AppendSectionCodeRefs(document, items);
        return items;
    }

    /// <summary>
    /// The frontmatter's keywords, or its aliases when it declares no keywords, followed by every
    /// section's keywords.
    /// </summary>
    public static IReadOnlyList<string> Keywords(this DocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<string> items = new();
        if (document.Frontmatter.Keywords is not null)
        {
            items.AddRange(document.Frontmatter.Keywords);
        }
        else if (document.Frontmatter.Aliases is not null)
        {
            items.AddRange(document.Frontmatter.Aliases);
        }

        AppendSectionKeywords(document, items);
        return items;
    }

    private static void AppendSectionKeywords(DocumentModel document, List<string> items)
    {
        if (document.Frontmatter.Sections is null)
        {
            return;
        }

        foreach (DocumentSectionVocabulary section in document.Frontmatter.Sections.Values)
        {
            if (section is null)
            {
                continue;
            }

            if (section.Keywords is not null)
            {
                items.AddRange(section.Keywords);
            }
        }
    }

    private static void AppendSectionCodeRefs(DocumentModel document, List<string> items)
    {
        if (document.Frontmatter.Sections is null)
        {
            return;
        }

        foreach (DocumentSectionVocabulary section in document.Frontmatter.Sections.Values)
        {
            if (section is null)
            {
                continue;
            }

            if (section.CodeRefs is not null)
            {
                items.AddRange(section.CodeRefs);
            }
        }
    }
}
