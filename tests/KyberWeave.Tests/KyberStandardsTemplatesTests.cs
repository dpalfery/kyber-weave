using System.Text.RegularExpressions;
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
        "go",
        "maui",
        "pulumi",
        "python",
        "react",
        "rust",
        "sql",
        "tauri",
        "test",
        "typescript"
    ];

    private static readonly string[] InnerLoopCommands = ["build", "test", "format"];

    /// <summary>One argument per embedded template, so a template added later is covered without an edit here.</summary>
    public static IEnumerable<object[]> DeclaredTechnologies() =>
        KyberStandardsTemplates.All.Select(technology => new object[] { technology });

    /// <summary>
    /// Every embedded coding standard carries exactly one <c>## Completion</c> section.
    /// </summary>
    /// <remarks>
    /// A finishing worker needs one place to learn how to prove its work. D5 puts that knowledge in the
    /// standard rather than in the portable agent files, which sent it to every host on every round trip and
    /// let it drift per project; D31 makes the section the only home by renaming <c>## Commands</c> to it.
    /// The list of technologies comes from <see cref="KyberStandardsTemplates.All"/>, so a standard added to
    /// the shipped set cannot silently omit the section.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DeclaredTechnologies))]
    public void EveryEmbeddedStandardCarriesOneCompletionSection(string technology)
    {
        string template = Template(technology);

        int count = SectionHeadings(template).Count(heading => heading == "Completion");

        Assert.True(
            count == 1,
            $"The '{technology}' standard template declares {count} '## Completion' sections. Add exactly one: " +
            "it carries the inner-loop commands a finishing worker runs, what runs when, and why.");
    }

    /// <summary>
    /// The <c>## Completion</c> section names every inner-loop command: build, test and format.
    /// </summary>
    /// <remarks>
    /// The section exists to answer "how do I finish"; a section that omits one of the three leaves the worker
    /// to guess, which is what the portable files used to do for them. A standard with no build or no formatter
    /// says so in the section rather than leaving it out, so the reader learns the answer either way.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DeclaredTechnologies))]
    public void TheCompletionSectionNamesEveryInnerLoopCommand(string technology)
    {
        string section = Section(Template(technology), "Completion");

        Assert.False(
            string.IsNullOrWhiteSpace(section),
            $"The '{technology}' standard template has no '## Completion' section, so there is nothing to read.");
        string[] missing = InnerLoopCommands
            .Where(command => !Regex.IsMatch(section, $@"\b{Regex.Escape(command)}\b", RegexOptions.IgnoreCase))
            .ToArray();
        Assert.True(
            missing.Length == 0,
            $"The '{technology}' standard template's '## Completion' section never names " +
            $"{string.Join(", ", missing)}. Say what a finishing worker runs, and say so when a command does " +
            "not apply to this stack.");
    }

    /// <summary>
    /// No embedded standard still keeps its inner-loop knowledge in a <c>## Commands</c> section.
    /// </summary>
    /// <remarks>
    /// Two sections answering the same question is how the answer drifts apart: one is edited, the other is
    /// not, and the worker reads whichever it loads. D31 renames the section rather than adding a second one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(DeclaredTechnologies))]
    public void NoEmbeddedStandardKeepsACommandsSectionNextToCompletion(string technology)
    {
        Assert.DoesNotContain(
            "Commands",
            SectionHeadings(Template(technology)),
            StringComparer.Ordinal);
    }

    private static string Template(string technology)
    {
        bool found = KyberStandardsTemplates.TryGet(technology, out string? template);

        Assert.True(found, $"Expected template for '{technology}' to be found in embedded resources.");
        Assert.NotNull(template);
        return template;
    }

    /// <summary>The level-two headings of a Markdown document, without the leading hashes.</summary>
    private static IReadOnlyList<string> SectionHeadings(string markdown)
    {
        return markdown
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())
            .ToArray();
    }

    /// <summary>
    /// One section's body: from its <c>##</c> heading to the next level-two heading. Empty when absent.
    /// </summary>
    private static string Section(string markdown, string heading)
    {
        string[] lines = markdown.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        int start = Array.FindIndex(
            lines,
            line => string.Equals(line.Trim(), "## " + heading, StringComparison.Ordinal));
        if (start < 0)
            return string.Empty;

        int end = Array.FindIndex(lines, start + 1, line => line.StartsWith("## ", StringComparison.Ordinal));

        return string.Join("\n", end < 0 ? lines[start..] : lines[start..end]);
    }

    /// <summary>
    /// Kyber-Weave embeds all 14 canonical Kyber Squad coding standards templates.
    /// </summary>
    [Fact]
    public void AllEnumeratesAllFourteenCanonicalTechnologies()
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
    [InlineData("go")]
    [InlineData("maui")]
    [InlineData("pulumi")]
    [InlineData("python")]
    [InlineData("react")]
    [InlineData("rust")]
    [InlineData("sql")]
    [InlineData("tauri")]
    [InlineData("test")]
    [InlineData("typescript")]
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
    /// Rendering a misspelled technology throws an ArgumentException with a nearest-match hint.
    /// </summary>
    [Fact]
    public void RenderThrowsWithDidYouMeanHintOnTypo()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => KyberStandardsTemplates.Render("cshar", "owner", "2026-08-17"));
        Assert.Contains("Did you mean 'csharp'?", ex.Message, StringComparison.Ordinal);
        Assert.Equal("technology", ex.ParamName);
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
