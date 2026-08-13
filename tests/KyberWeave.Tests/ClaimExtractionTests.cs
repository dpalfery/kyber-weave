using KyberWeave.Core.Docs.Analysis.Claims;
using KyberWeave.Core.Docs.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the structural claim boundary used by duplicate, conflict, and terminology
/// analysis. A claim must remain traceable to source even though retrieval stores a
/// frontmatter-free body.
/// </summary>
public sealed class ClaimExtractionTests
{
    [Fact]
    public void Extract_WithSupportedMarkdownBlocks_ReturnsLineAddressableClaimsInSourceOrder()
    {
        const string body = """
            # Claim extraction

            ## Runtime

            First paragraph wraps onto
            its second source line.

            - Direct list item.

            | Term | Meaning |
            | --- | --- |
            | loop | gameplay wrapper |

            ```csharp title="sample"
            Console.WriteLine("loop");
            ```

            ~~~json
            {"loop":"codex"}
            ~~~
            """;
        var document = Document(body);

        var result = new ClaimExtractor().Extract(document);

        Assert.Empty(result.Diagnostics.Items);
        Assert.Collection(
            result.Claims,
            claim => AssertClaim(
                claim,
                ClaimKind.Paragraph,
                "First paragraph wraps onto its second source line.",
                13,
                14),
            claim => AssertClaim(claim, ClaimKind.ListItem, "Direct list item.", 16, 16),
            claim =>
            {
                AssertClaim(claim, ClaimKind.TableRow, "loop | gameplay wrapper", 20, 20);
                Assert.Contains("Term: loop", claim.ContextualText, StringComparison.Ordinal);
                Assert.Contains("Meaning: gameplay wrapper", claim.ContextualText, StringComparison.Ordinal);
            },
            claim =>
            {
                AssertClaim(claim, ClaimKind.CodeBlock, "Console.WriteLine(\"loop\");", 22, 24);
                Assert.Contains("csharp title=\"sample\"", claim.ContextualText, StringComparison.Ordinal);
            },
            claim =>
            {
                AssertClaim(claim, ClaimKind.CodeBlock, "{\"loop\":\"codex\"}", 26, 28);
                Assert.Contains("json", claim.ContextualText, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Extract_WithSameProseUnderDifferentSections_SeparatesContentAndContextHashes()
    {
        const string body = """
            # Hashes

            ## Gameplay

            Run the Loop now.

            ## Automation

            run the loop now
            """;

        var claims = new ClaimExtractor().Extract(Document(body)).Claims;

        Assert.Equal(2, claims.Count);
        Assert.Equal(claims[0].ContentHash, claims[1].ContentHash);
        Assert.NotEqual(claims[0].ContextualHash, claims[1].ContextualHash);
        Assert.StartsWith("Gameplay", claims[0].ContextualText, StringComparison.Ordinal);
        Assert.StartsWith("Automation", claims[1].ContextualText, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_WithCaseDistinctCode_DoesNotApplyEnglishProseNormalizationToCode()
    {
        const string body = """
            # Hashes

            ## Runtime

            ```csharp
            Loop.Run();
            ```

            ```csharp
            loop.Run();
            ```
            """;

        var claims = new ClaimExtractor().Extract(Document(body)).Claims;

        Assert.Equal(2, claims.Count);
        Assert.All(claims, claim => Assert.Equal(ClaimKind.CodeBlock, claim.Kind));
        Assert.NotEqual(claims[0].ContentHash, claims[1].ContentHash);
    }

    [Fact]
    public void Extract_WithInlineCodeAndLiteralPlaceholderText_RestoresFenceWithoutColliding()
    {
        const string body = """
            # Claims

            ## Runtime

            Run `dotnet test` even if the prose mentions KYBERINLINELITERAL0END.
            """;

        var claim = Assert.Single(new ClaimExtractor().Extract(Document(body)).Claims);

        Assert.Contains("`dotnet test`", claim.ContextualText, StringComparison.Ordinal);
        Assert.Contains("KYBERINLINELITERAL0END", claim.ContextualText, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet testEND", claim.ContextualText, StringComparison.Ordinal);
    }

    private static void AssertClaim(
        Claim claim,
        ClaimKind expectedKind,
        string expectedText,
        int expectedStartLine,
        int expectedEndLine)
    {
        Assert.Equal(expectedKind, claim.Kind);
        Assert.Equal(expectedText, claim.Text);
        Assert.Equal("Runtime", claim.Section);
        Assert.Equal(expectedStartLine, claim.StartLine);
        Assert.Equal(expectedEndLine, claim.EndLine);
        Assert.Equal("docs/claims", claim.DocumentIdentity);
        Assert.Equal("DocGraph", claim.Component);
        Assert.False(string.IsNullOrWhiteSpace(claim.ContentHash));
        Assert.False(string.IsNullOrWhiteSpace(claim.ContextualHash));
        Assert.Contains("Runtime", claim.ContextualText, StringComparison.Ordinal);
    }

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
            FilePath = "/repo/docs/claims.md",
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
