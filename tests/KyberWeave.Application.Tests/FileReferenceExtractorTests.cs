using KyberWeave.Core.Parsing;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Markdig;
using Markdig.Syntax;
using Xunit;

namespace KyberWeave.Tests;

public class FileReferenceExtractorTests
{
    private static readonly string Root = Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", "work");

    private readonly InMemoryFileSystem files = new();

    public FileReferenceExtractorTests() => files.CreateDirectory(Root);

    private FileReferenceExtractor Extractor => Over(files);

    private static FileReferenceExtractor Over(InMemoryFileSystem fileSystem) =>
        new(fileSystem, new FakePathResolver(fileSystem));

    [Fact]
    public void MarkdownLinkInlineTargetIsExtractedAsReference()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "Check the [documentation](references/guide.md) for details.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
        Assert.Equal(Path.Combine(Root, "references", "guide.md"), reference.ResolvedFullPath);
        Assert.False(reference.IsPathTraversal);
        Assert.Null(reference.NearestMatch);
    }

    [Fact]
    public void CompleteCodeInlineSpanIsExtractedInCodeInlineOnlyMode()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See `references/guide.md` for guidance.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
    }

    [Fact]
    public void UnfencedTextPathIsIgnoredInCodeInlineOnlyMode()
    {
        string markdown =
            "Plain references/plain.md, `prefix references/partial.md`, and ```\nreferences/block.md\n```";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void UnfencedTextPathIsExtractedInUnfencedTextMode()
    {
        string scriptsDir = Path.Combine(Root, "scripts");
        files.CreateDirectory(scriptsDir);
        files.WriteAllText(Path.Combine(scriptsDir, "run.py"), "# python script");

        string markdown = "See scripts/run.py for details.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.SkillDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("scripts/run.py", reference.Reference);
        Assert.True(reference.Exists);
    }

    /// <summary>
    /// Regression (issue 134): UnfencedText mode scans the raw markdown, so the tail of an
    /// https link destination matched UnfencedPathRegex and leaked in as a reference — the
    /// match begins after the scheme, so the URL filter never sees the destination.
    /// </summary>
    [Fact]
    public void HttpsLinkDestinationIsNotExtractedInUnfencedTextMode()
    {
        string markdown = "See the [docs](https://example.com/references/missing.md) online.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.SkillDefault);

        Assert.DoesNotContain(results, r => r.Reference == "references/missing.md");
    }

    /// <summary>
    /// Regression (issue 134): the AST-based UnfencedText scan must keep extracting inline
    /// code spans (e.g. LOAD `references/rules.md`), which the previous raw-markdown scan
    /// covered and which the skill validator relies on for broken-reference detection.
    /// </summary>
    [Fact]
    public void InlineCodeSpanPathIsExtractedInUnfencedTextMode()
    {
        string markdown = "LOAD `references/rules.md` before continuing.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.SkillDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/rules.md", reference.Reference);
        Assert.False(reference.Exists);
    }

    /// <summary>
    /// Regression (issue 134): UnfencedText mode scans the raw markdown, so a path inside a
    /// fenced code block matched even though fenced content is not unfenced body text.
    /// </summary>
    [Fact]
    public void FencedCodeBlockPathIsNotExtractedInUnfencedTextMode()
    {
        string markdown = "Example usage:\n\n```\nreferences/block.md\n```";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.SkillDefault);

        Assert.DoesNotContain(results, r => r.Reference == "references/block.md");
    }

    /// <summary>
    /// Regression (issue 134): a bare https URL in prose is unfenced text, so UnfencedPathRegex
    /// matched its path-like tail and the tail leaked in as a reference. Matches that fall inside
    /// a URL span (scheme to the next whitespace or end of content) must be skipped.
    /// </summary>
    [Fact]
    public void BareUrlPathIsNotExtractedInUnfencedTextMode()
    {
        string markdown = "Visit https://example.com/references/missing.md for details.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.SkillDefault);

        Assert.DoesNotContain(results, r => r.Reference == "references/missing.md");
    }

    /// <summary>
    /// Positive control for <see cref="BareUrlPathIsNotExtractedInUnfencedTextMode"/>: the
    /// identical path tail outside a URL span must still be extracted, and must not be reported
    /// as existing — guards the URL-span skip against suppressing plain prose paths.
    /// </summary>
    [Fact]
    public void BarePathToMissingFileIsExtractedInUnfencedTextMode()
    {
        string markdown = "See references/missing.md for details.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.SkillDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/missing.md", reference.Reference);
        Assert.False(reference.Exists);
    }

    /// <summary>
    /// Regression (issue 134): inline code spans are on the UnfencedText scan surface, so a URL
    /// inside a code span matched UnfencedPathRegex on its path tail. The URL-span skip applies
    /// to every scanned surface, code spans included.
    /// </summary>
    [Fact]
    public void BareUrlInsideCodeSpanIsNotExtractedInUnfencedTextMode()
    {
        string markdown = "LOAD `https://example.com/references/block.md` for details.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.SkillDefault);

        Assert.DoesNotContain(results, r => r.Reference == "references/block.md");
    }

    /// <summary>
    /// Regression (issue 134): a link destination containing characters that are invalid in
    /// Windows path names (a double quote) extracts as a non-existent reference and never
    /// aborts extraction. No portable input reaches the extended catch clauses in
    /// FindNearestMatch — their parity with the sibling path-resolution catches is enforced by
    /// inspection, not by this test.
    /// </summary>
    [Fact]
    public void InvalidPathCharacterInLinkDestinationDoesNotAbortExtraction()
    {
        string markdown = "[x](refer\"ences/guide.md)";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.SkillDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("refer\"ences/guide.md", reference.Reference);
        Assert.False(reference.Exists);
    }

    [Fact]
    public void LeadingDotSlashPrefixIsNormalized()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See [guide](./references/guide.md).";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
    }

    [Fact]
    public void FragmentAnchorIsStrippedAndTargetFileResolves()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See [overview](references/guide.md#overview) for details.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
    }

    [Fact]
    public void UrlsMailtoAndAnchorOnlyLinksAreExcluded()
    {
        string markdown =
            "[web](https://example.com/doc) [insecure](http://example.com/doc) [upper](HTTP://example.com) " +
            "[mail](mailto:dev@example.com) [upper mail](MAILTO:dev@example.com) [anchor](#overview)";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void ConfigRegTokensAreExcluded()
    {
        string markdown = "Refer to `<docs-root>` and `<plan-index>` or [Docs](<docs-root>).";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void ForeignAndAbsolutePathsAreExcluded()
    {
        string markdown =
            "[drive](C:/foo/bar.md) [posix](/etc/bar.md) [unc](\\\\share\\path\\file.md) [unc-slash](//unc/path/file.md)";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void PathTraversalIsSkippedWhenSkipPathTraversalIsTrue()
    {
        string markdown = "See [parent](../secret.md) and `../../outside.txt`.";
        IReadOnlyList<ExtractedFileReference> results = Extractor.ExtractFromText(
            markdown,
            Root,
            new FileReferenceOptions { SkipPathTraversal = true, InlineScanMode = InlinePathScanMode.CodeInlineOnly });

        Assert.Empty(results);
    }

    [Fact]
    public void PathTraversalIsReportedWithFlagWhenSkipPathTraversalIsFalse()
    {
        string markdown = "See [parent](../secret.md) and [escape](../../outside.txt).";
        IReadOnlyList<ExtractedFileReference> results = Extractor.ExtractFromText(
            markdown,
            Root,
            new FileReferenceOptions { SkipPathTraversal = false });

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.True(r.IsPathTraversal);
            Assert.False(r.Exists);
        });
    }

    [Fact]
    public void NearestMatchSuggestsExistingFileWithinEditDistance()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See `references/guid.md`.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guid.md", reference.Reference);
        Assert.False(reference.Exists);
        Assert.Equal("references/guide.md", reference.NearestMatch);
    }

    [Fact]
    public void NearestMatchReturnsNullWhenDistanceExceedsThreshold()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See `references/completely-different-name.md`.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/completely-different-name.md", reference.Reference);
        Assert.False(reference.Exists);
        Assert.Null(reference.NearestMatch);
    }

    [Fact]
    public void NearestMatchReturnsNullWhenDirectoryDoesNotExist()
    {
        string markdown = "See `nonexistent/file.md`.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("nonexistent/file.md", reference.Reference);
        Assert.False(reference.Exists);
        Assert.Null(reference.NearestMatch);
    }

    [Fact]
    public void NearestMatchNeverEscapesParentDirectory()
    {
        string agentDir = Path.Combine(Root, "agent");
        files.CreateDirectory(Path.Combine(agentDir, "references"));
        files.WriteAllText(Path.Combine(Root, "guide.md"), "# Guide outside agent dir");

        string markdown = "See `references/guide.md`.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, agentDir, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.False(reference.Exists);
        Assert.Null(reference.NearestMatch);
    }

    [Fact]
    public void ExtractFromTextsAggregatesReferencesAcrossMultipleInputs()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "one.md"), "# One");
        files.WriteAllText(Path.Combine(referencesDir, "two.md"), "# Two");

        string[] texts = ["See `references/one.md`.", "See `references/two.md`."];
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromTexts(texts, Root, FileReferenceOptions.AgentDefault);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Reference == "references/one.md" && r.Exists);
        Assert.Contains(results, r => r.Reference == "references/two.md" && r.Exists);
    }

    [Fact]
    public void ExtractFromDocumentReusesParsedAst()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string raw = "See [guide](references/guide.md).";
        MarkdownDocument document = Markdown.Parse(raw);
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromDocument(document, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
    }

    [Fact]
    public void AngleBracketPathToMissingFileIsExtractedWithExistsFalse()
    {
        string markdown = "Check the [Guide](<references/missing guide.md>) for details.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/missing guide.md", reference.Reference);
        Assert.False(reference.Exists);
    }

    [Fact]
    public void AngleBracketConfigRegTokenIsSkipped()
    {
        string markdown = "Refer to [Docs](<docs-root>) for documentation.";
        IReadOnlyList<ExtractedFileReference> results =
            Extractor.ExtractFromText(markdown, Root, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void WrongCaseReferenceDoesNotResolveOnACaseSensitiveFileSystem()
    {
        InMemoryFileSystem sensitive = new(caseSensitive: true);
        sensitive.CreateDirectory(Path.Combine(Root, "references"));
        sensitive.WriteAllText(Path.Combine(Root, "references", "Guide.md"), "# Guide");

        IReadOnlyList<ExtractedFileReference> results =
            Over(sensitive).ExtractFromText("See `references/guide.md`.", Root, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.False(reference.Exists);
        Assert.Equal("references/Guide.md", reference.NearestMatch);
    }

    [Fact]
    public void WrongCaseReferenceResolvesOnACaseInsensitiveFileSystem()
    {
        InMemoryFileSystem insensitive = new(caseSensitive: false);
        insensitive.CreateDirectory(Path.Combine(Root, "references"));
        insensitive.WriteAllText(Path.Combine(Root, "references", "Guide.md"), "# Guide");

        IReadOnlyList<ExtractedFileReference> results =
            Over(insensitive).ExtractFromText("See `references/guide.md`.", Root, FileReferenceOptions.AgentDefault);

        Assert.True(Assert.Single(results).Exists);
    }
}
