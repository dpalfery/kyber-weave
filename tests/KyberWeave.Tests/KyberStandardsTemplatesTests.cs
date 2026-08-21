using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Parsing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies the embedded Kyber Squad coding standards templates.
/// </summary>
public sealed class KyberStandardsTemplatesTests
{
    private static readonly string[] ExpectedTechnologies =
    [
        "azure",
        "csharp",
        "data-access-layer",
        "github-actions",
        "maui",
        "pulumi",
        "python",
        "react",
        "sql",
        "test"
    ];

    /// <summary>
    /// Kyber-Weave embeds all 10 canonical Kyber Squad coding standards templates.
    /// </summary>
    [Fact]
    public void AllEnumeratesAllTenCanonicalTechnologies()
    {
        IReadOnlyList<string> all = KyberStandardsTemplates.All;

        Assert.Equal(ExpectedTechnologies.Length, all.Count);
        Assert.Equal(ExpectedTechnologies.OrderBy(t => t), all.OrderBy(t => t));
    }

    /// <summary>
    /// Each declared technology has an embedded template that can be retrieved as raw markdown.
    /// </summary>
    [Theory]
    [InlineData("azure")]
    [InlineData("csharp")]
    [InlineData("data-access-layer")]
    [InlineData("github-actions")]
    [InlineData("maui")]
    [InlineData("pulumi")]
    [InlineData("python")]
    [InlineData("react")]
    [InlineData("sql")]
    [InlineData("test")]
    public void TryGetRetrievesTemplateForEachDeclaredTechnology(string technology)
    {
        bool found = KyberStandardsTemplates.TryGet(technology, out string? rawTemplate);

        Assert.True(found, $"Expected template for '{technology}' to be found in embedded resources.");
        Assert.NotNull(rawTemplate);
        Assert.StartsWith("---", rawTemplate, StringComparison.Ordinal);
        Assert.Contains($"technology: {technology}", rawTemplate, StringComparison.Ordinal);
        Assert.Contains("doc-type: coding-standard", rawTemplate, StringComparison.Ordinal);
    }

    /// <summary>
    /// Looking up an unknown technology returns false and null.
    /// </summary>
    [Fact]
    public void TryGetReturnsFalseForUnknownTechnology()
    {
        bool found = KyberStandardsTemplates.TryGet("unknown-technology", out string? rawTemplate);

        Assert.False(found);
        Assert.Null(rawTemplate);
    }

    /// <summary>
    /// Rendering replaces the frontmatter owner and last-reviewed date while preserving
    /// the rest of the template intact.
    /// </summary>
    [Fact]
    public void RenderInjectsCustomOwnerAndDateIntoFrontmatter()
    {
        const string owner = "alice";
        const string date = "2026-08-17";

        string rendered = KyberStandardsTemplates.Render("csharp", owner, date);

        Assert.Contains("owner: 'alice'", rendered, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: '2026-08-17'", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("owner: unassigned", rendered, StringComparison.Ordinal);
        Assert.Contains("# C# coding standard", rendered, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", rendered, StringComparison.Ordinal);
        Assert.Contains("doc-type: coding-standard", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// YAML punctuation in an accepted owner must remain a scalar. Unquoted interpolation
    /// turns a colon into a nested mapping and a hash into a comment — the same values
    /// DocsScaffolder.Scaffold already accepts for stub standards.
    /// </summary>
    [Fact]
    public void RenderYamlEncodesOwnerAndDateSoFrontmatterStaysValid()
    {
        const string owner = "platform's: \"core\" #1";
        const string date = "2026-08-17";

        string rendered = KyberStandardsTemplates.Render("csharp", owner, date);

        FrontmatterReadResult read = MarkdownFrontmatterReader.Read(rendered);
        Assert.True(read.HasFrontmatter);

        DocumentFrontmatter frontmatter = MarkdownFrontmatterReader.Deserializer
            .Deserialize<DocumentFrontmatter>(read.Yaml);

        Assert.Equal(owner, frontmatter.Owner);
        Assert.Equal(date, frontmatter.LastReviewed);
        Assert.Contains("owner: " + HostConfigYaml.QuoteScalar(owner), rendered, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: " + HostConfigYaml.QuoteScalar(date), rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rendering an unknown technology throws an ArgumentException.
    /// </summary>
    [Fact]
    public void RenderThrowsOnUnknownTechnology()
    {
        Assert.Throws<ArgumentException>(() => KyberStandardsTemplates.Render("nonexistent", "owner", "2026-08-17"));
    }

    /// <summary>
    /// TryRender successfully renders a known technology template.
    /// </summary>
    [Fact]
    public void TryRenderRendersKnownTechnology()
    {
        bool found = KyberStandardsTemplates.TryRender("csharp", "alice", "2026-08-17", out string? rendered);

        Assert.True(found);
        Assert.NotNull(rendered);
        Assert.Contains("owner: 'alice'", rendered, StringComparison.Ordinal);
        Assert.Contains("last-reviewed: '2026-08-17'", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// TryRender returns false and null for an unknown technology.
    /// </summary>
    [Fact]
    public void TryRenderReturnsFalseForUnknownTechnology()
    {
        bool found = KyberStandardsTemplates.TryRender("unknown-tech", "alice", "2026-08-17", out string? rendered);

        Assert.False(found);
        Assert.Null(rendered);
    }

    /// <summary>
    /// TryRender returns false and null when technology is null.
    /// </summary>
    [Fact]
    public void TryRenderReturnsFalseForNullTechnology()
    {
        bool found = KyberStandardsTemplates.TryRender(null, "alice", "2026-08-17", out string? rendered);

        Assert.False(found);
        Assert.Null(rendered);
    }
}
