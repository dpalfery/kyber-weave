using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Claims;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Ignore markup is deliberately strict: malformed suppression must become an
/// operational error instead of silently allowing a finding to disappear.
/// </summary>
public sealed class IgnoreMarkupTests
{
    [Theory]
    [InlineData("duplicate", IgnoreRule.Duplicate)]
    [InlineData("conflict", IgnoreRule.Conflict)]
    [InlineData("terminology", IgnoreRule.Terminology)]
    [InlineData("all", IgnoreRule.All)]
    public void Extract_WithAnExactIgnoreRule_MarksOnlyTheWrappedClaim(
        string rule,
        IgnoreRule expectedRule)
    {
        var body = $$"""
            # Ignores

            ## Runtime

            <kyber-ignore rule="{{rule}}">
            The gameplay loop runs live tests.
            </kyber-ignore>

            The Codex loop consumes model tokens.
            """;
        var originalBody = body;
        var document = ClaimExtractionTests.Document(body);

        var result = new ClaimExtractor().Extract(document);

        Assert.Empty(result.Diagnostics.Items);
        Assert.Equal(2, result.Claims.Count);
        Assert.Equal(expectedRule, result.Claims[0].IgnoreRules);
        Assert.Equal(IgnoreRule.None, result.Claims[1].IgnoreRules);
        Assert.Equal("The gameplay loop runs live tests.", result.Claims[0].Text);
        Assert.Equal(originalBody, document.Body);
    }

    [Theory]
    [InlineData("<kyber-ignore>\nSuppressed prose.\n</kyber-ignore>")]
    [InlineData("<kyber-ignore rule=\"Duplicate\">\nSuppressed prose.\n</kyber-ignore>")]
    [InlineData("<Kyber-ignore rule=\"duplicate\">\nSuppressed prose.\n</Kyber-ignore>")]
    [InlineData("<kyber-ignore rule=\"unknown\">\nSuppressed prose.\n</kyber-ignore>")]
    [InlineData("<kyber-ignore rule=duplicate>\nSuppressed prose.\n</kyber-ignore>")]
    [InlineData("<kyber-ignore rule=\"duplicate\">\nSuppressed prose.")]
    [InlineData("Suppressed prose.\n</kyber-ignore>")]
    public void Extract_WithMalformedUnknownCaseChangedOrUnbalancedMarkup_ReportsOperationalError(
        string markup)
    {
        var body = $$"""
            # Invalid ignores

            ## Runtime

            {{markup}}
            """;

        var result = new ClaimExtractor().Extract(ClaimExtractionTests.Document(body));

        AssertOperationalIgnoreError(result.Diagnostics.Items);
    }

    [Fact]
    public void Extract_WithNestedIgnoreMarkup_ReportsOperationalError()
    {
        const string body = """
            # Invalid ignores

            ## Runtime

            <kyber-ignore rule="duplicate">
            <kyber-ignore rule="conflict">
            Suppressed prose.
            </kyber-ignore>
            </kyber-ignore>
            """;

        var result = new ClaimExtractor().Extract(ClaimExtractionTests.Document(body));

        AssertOperationalIgnoreError(result.Diagnostics.Items);
    }

    [Fact]
    public void Extract_WithIgnoreMarkupCrossingASectionBoundary_ReportsOperationalError()
    {
        const string body = """
            # Invalid ignores

            ## Runtime

            <kyber-ignore rule="duplicate">
            Suppressed prose.

            ## Automation

            Other prose.
            </kyber-ignore>
            """;

        var result = new ClaimExtractor().Extract(ClaimExtractionTests.Document(body));

        AssertOperationalIgnoreError(result.Diagnostics.Items);
    }

    [Fact]
    public void Extract_WithIgnoreMarkupCrossingFrontmatter_ReportsOperationalError()
    {
        const string rawMarkdown = """
            ---
            id: docs/claims
            title: Claims
            doc-type: reference
            status: current
            component: DocGraph
            note: <kyber-ignore rule="duplicate">
            ---

            # Invalid ignores

            ## Runtime

            Suppressed prose.
            </kyber-ignore>
            """;
        const string body = """
            # Invalid ignores

            ## Runtime

            Suppressed prose.
            </kyber-ignore>
            """;

        var document = ClaimExtractionTests.Document(body, rawMarkdown, bodyStartLine: 10);

        var result = new ClaimExtractor().Extract(document);

        AssertOperationalIgnoreError(result.Diagnostics.Items);
    }

    [Fact]
    public void Extract_WithTagLikeTextInsideFences_TreatsItAsCodeInsteadOfMarkup()
    {
        const string body = """
            # Ignore examples

            ## Runtime

            ```html
            <kyber-ignore rule="unknown">
            Example only.
            </kyber-ignore>
            ```

            ~~~text
            <Kyber-ignore rule="duplicate">also an example</Kyber-ignore>
            ~~~
            """;

        var result = new ClaimExtractor().Extract(ClaimExtractionTests.Document(body));

        Assert.Empty(result.Diagnostics.Items);
        Assert.Equal(2, result.Claims.Count);
        Assert.All(result.Claims, claim =>
        {
            Assert.Equal(ClaimKind.CodeBlock, claim.Kind);
            Assert.Equal(IgnoreRule.None, claim.IgnoreRules);
        });
    }

    [Fact]
    public void Extract_WithIgnoreMarkup_DoesNotMutateTheRetrievalBody()
    {
        const string body = """
            # Ignores

            ## Runtime

            <kyber-ignore rule="all">
            Preserve this original body verbatim.
            </kyber-ignore>
            """;
        var document = ClaimExtractionTests.Document(body);

        _ = new ClaimExtractor().Extract(document);

        Assert.Equal(body, document.Body);
    }

    private static void AssertOperationalIgnoreError(IReadOnlyList<Diagnostic> diagnostics)
    {
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("KW-DOC-ANALYSIS-004", diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Hint));
    }
}
