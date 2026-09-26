using KyberWeave.Core.Parsing;
using Xunit;

namespace KyberWeave.Tests;

public class FileReferenceExtractorTests
{
    [Fact]
    public void MarkdownLinkInlineTargetIsExtractedAsReference()
    {
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "Check the [documentation](references/guide.md) for details.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
        Assert.Equal(Path.Combine(tempDir.Path, "references", "guide.md"), reference.ResolvedFullPath);
        Assert.False(reference.IsPathTraversal);
        Assert.Null(reference.NearestMatch);
    }

    [Fact]
    public void CompleteCodeInlineSpanIsExtractedInCodeInlineOnlyMode()
    {
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See `references/guide.md` for guidance.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
    }

    [Fact]
    public void UnfencedTextPathIsIgnoredInCodeInlineOnlyMode()
    {
        using TempDirectory tempDir = new TempDirectory();
        string markdown = "Plain references/plain.md, `prefix references/partial.md`, and ```\nreferences/block.md\n```";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void UnfencedTextPathIsExtractedInUnfencedTextMode()
    {
        using TempDirectory tempDir = new TempDirectory();
        string scriptsDir = Path.Combine(tempDir.Path, "scripts");
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "run.py"), "# python script");

        string markdown = "See scripts/run.py for details.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.SkillDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("scripts/run.py", reference.Reference);
        Assert.True(reference.Exists);
    }

    [Fact]
    public void LeadingDotSlashPrefixIsNormalized()
    {
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See [guide](./references/guide.md).";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
    }

    [Fact]
    public void FragmentAnchorIsStrippedAndTargetFileResolves()
    {
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See [overview](references/guide.md#overview) for details.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
    }

    [Fact]
    public void UrlsMailtoAndAnchorOnlyLinksAreExcluded()
    {
        using TempDirectory tempDir = new TempDirectory();
        string markdown = "[web](https://example.com/doc) [insecure](http://example.com/doc) [upper](HTTP://example.com) " +
                          "[mail](mailto:dev@example.com) [upper mail](MAILTO:dev@example.com) [anchor](#overview)";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void ConfigRegTokensAreExcluded()
    {
        using TempDirectory tempDir = new TempDirectory();
        string markdown = "Refer to `<docs-root>` and `<plan-index>` or [Docs](<docs-root>).";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void ForeignAndAbsolutePathsAreExcluded()
    {
        using TempDirectory tempDir = new TempDirectory();
        string markdown = "[drive](C:/foo/bar.md) [posix](/etc/bar.md) [unc](\\\\share\\path\\file.md) [unc-slash](//unc/path/file.md)";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        Assert.Empty(results);
    }

    [Fact]
    public void PathTraversalIsSkippedWhenSkipPathTraversalIsTrue()
    {
        using TempDirectory tempDir = new TempDirectory();
        string markdown = "See [parent](../secret.md) and `../../outside.txt`.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(
            markdown,
            tempDir.Path,
            new FileReferenceOptions { SkipPathTraversal = true, InlineScanMode = InlinePathScanMode.CodeInlineOnly });

        Assert.Empty(results);
    }

    [Fact]
    public void PathTraversalIsReportedWithFlagWhenSkipPathTraversalIsFalse()
    {
        using TempDirectory tempDir = new TempDirectory();
        string markdown = "See [parent](../secret.md) and [escape](../../outside.txt).";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(
            markdown,
            tempDir.Path,
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
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See `references/guid.md`.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guid.md", reference.Reference);
        Assert.False(reference.Exists);
        Assert.Equal("references/guide.md", reference.NearestMatch);
    }

    [Fact]
    public void NearestMatchReturnsNullWhenDistanceExceedsThreshold()
    {
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string markdown = "See `references/completely-different-name.md`.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/completely-different-name.md", reference.Reference);
        Assert.False(reference.Exists);
        Assert.Null(reference.NearestMatch);
    }

    [Fact]
    public void NearestMatchReturnsNullWhenDirectoryDoesNotExist()
    {
        using TempDirectory tempDir = new TempDirectory();
        string markdown = "See `nonexistent/file.md`.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, tempDir.Path, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("nonexistent/file.md", reference.Reference);
        Assert.False(reference.Exists);
        Assert.Null(reference.NearestMatch);
    }

    [Fact]
    public void NearestMatchNeverEscapesParentDirectory()
    {
        using TempDirectory tempDir = new TempDirectory();
        string agentDir = Path.Combine(tempDir.Path, "agent");
        Directory.CreateDirectory(Path.Combine(agentDir, "references"));
        File.WriteAllText(Path.Combine(tempDir.Path, "guide.md"), "# Guide outside agent dir");

        string markdown = "See `references/guide.md`.";
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromText(markdown, agentDir, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.False(reference.Exists);
        Assert.Null(reference.NearestMatch);
    }

    [Fact]
    public void ExtractFromTextsAggregatesReferencesAcrossMultipleInputs()
    {
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "one.md"), "# One");
        File.WriteAllText(Path.Combine(referencesDir, "two.md"), "# Two");

        string[] texts = ["See `references/one.md`.", "See `references/two.md`."];
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromTexts(texts, tempDir.Path, FileReferenceOptions.AgentDefault);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Reference == "references/one.md" && r.Exists);
        Assert.Contains(results, r => r.Reference == "references/two.md" && r.Exists);
    }

    [Fact]
    public void ExtractFromDocumentReusesParsedAst()
    {
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        string raw = "See [guide](references/guide.md).";
        Markdig.Syntax.MarkdownDocument document = Markdig.Markdown.Parse(raw);
        IReadOnlyList<ExtractedFileReference> results = FileReferenceExtractor.ExtractFromDocument(document, raw, tempDir.Path, FileReferenceOptions.AgentDefault);

        ExtractedFileReference reference = Assert.Single(results);
        Assert.Equal("references/guide.md", reference.Reference);
        Assert.True(reference.Exists);
    }
}
