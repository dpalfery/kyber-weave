using KyberWeave.Core.Docs.Model;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests;

/// <summary>A parsed document for the claim extractor, built in memory: the extractor reads the model, never a file.</summary>
internal static class ClaimTestDocuments
{
    /// <summary>Builds the document model the claim tests extract from.</summary>
    /// <param name="body">The frontmatter-free body.</param>
    /// <param name="rawMarkdown">The whole file text; a default frontmatter block followed by <paramref name="body"/> when null.</param>
    /// <param name="bodyStartLine">The one-based line of the file the body starts on.</param>
    /// <returns>The document model.</returns>
    internal static DocumentModel Document(
        string body,
        string? rawMarkdown = null,
        int bodyStartLine = 9)
    {
        rawMarkdown ??= """
            ---
            id: docs/claims
            title: Claims
            doc-type: reference
            status: current
            component: DocGraph
            ---

            """ + body;

        return new DocumentModel
        {
            RelativePath = "docs/claims.md",
            FilePath = Path.Combine(new InMemoryFileSystem().Root, "repo", "docs", "claims.md"),
            HasFrontmatter = true,
            Frontmatter = new DocumentFrontmatter
            {
                Id = "docs/claims",
                Title = "Claims",
                DocType = "reference",
                Status = "current",
                Component = "DocGraph"
            },
            DocType = DocType.Reference,
            Status = DocStatus.Current,
            Body = body,
            RawMarkdown = rawMarkdown,
            BodyStartLine = bodyStartLine
        };
    }
}
