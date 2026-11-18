using System.Globalization;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.13 (bot comment triage): <c>FileTextFacts.ReadExcerpt</c> reads the current text around the
/// line a review comment cites, so the provider judges the code as it is now rather than as the bot saw it. The read
/// is bounded, and it never leaves the repository root.
/// RED: <c>KyberWeave.Core.Arbiter.Facts.FileTextFacts</c> and <c>FileExcerpt</c> do not exist yet.
/// </summary>
/// <remarks>
/// Names (GREEN must match): <c>InMemoryFileTextFacts.Create(_files).ReadExcerpt(string repoRoot, string path, int line)</c> returns
/// <c>FileExcerpt(string Path, int StartLine, int EndLine, string Text, bool Truncated)</c> or <c>null</c> when the
/// file is absent or outside the root; <c>FileTextFacts.MaxExcerptCharacters</c> bounds <c>Text</c>.
/// </remarks>
public sealed class FileTextFactsTests
{
    private readonly InMemoryFileSystem _files = new();

    private static string Root { get; } = ArbiterTestRoots.Under("repo");

    private string WriteRepoFile(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath);
        _files.CreateDirectory(Path.GetDirectoryName(full)!);
        _files.WriteAllText(full, content);
        return relativePath;
    }

    /// <summary>The excerpt is a window around the cited line, so the cited code is in it and the whole file is not.</summary>
    [Fact]
    public void TheExcerptWindowContainsTheCitedLineAndNotTheWholeFile()
    {
        string content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        string path = WriteRepoFile("src/Widget.cs", content);

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(InMemoryFileTextFacts.Create(_files).ReadExcerpt(Root, path, 50));

        Assert.True(excerpt.StartLine <= 50 && excerpt.EndLine >= 50, $"{excerpt.StartLine}-{excerpt.EndLine}");
        Assert.True(excerpt.EndLine - excerpt.StartLine < 40, $"{excerpt.StartLine}-{excerpt.EndLine}");
        Assert.Contains("line 50", excerpt.Text, StringComparison.Ordinal);
        Assert.False(excerpt.Truncated);
    }

    /// <summary>
    /// One enormous line cannot push an unbounded prompt into the provider call. Failure this guards: a generated file
    /// or a minified bundle turns every comment triage into a multi-megabyte request.
    /// </summary>
    [Fact]
    public void AnEnormousLineIsBoundedAndMarkedTruncated()
    {
        string path = WriteRepoFile("src/Generated.cs", "// header\n" + new string('x', 200_000) + "\n");

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(InMemoryFileTextFacts.Create(_files).ReadExcerpt(Root, path, 2));

        Assert.True(
            excerpt.Text.Length <= FileTextFacts.MaxExcerptCharacters,
            excerpt.Text.Length.ToString(CultureInfo.InvariantCulture));
        Assert.True(excerpt.Truncated);
    }

    /// <summary>
    /// A comment cites a path, and a path that climbs out of the root names a file the repository does not own. The
    /// excerpt is not read, and the caller treats the comment as unsure.
    /// </summary>
    [Fact]
    public void APathThatLeavesTheRootIsNotRead()
    {
        string outside = Path.Combine(ArbiterTestRoots.Under(), "outside.txt");
        _files.WriteAllText(outside, "secret\n");
        _files.CreateDirectory(Root);

        Assert.Null(InMemoryFileTextFacts.Create(_files).ReadExcerpt(Root, "../outside.txt", 1));
        Assert.Null(InMemoryFileTextFacts.Create(_files).ReadExcerpt(Root, outside, 1));
    }

    /// <summary>A file the branch has since deleted has no current text to judge a comment against.</summary>
    [Fact]
    public void AMissingCitedFileHasNoExcerpt()
    {
        _files.CreateDirectory(Root);

        Assert.Null(InMemoryFileTextFacts.Create(_files).ReadExcerpt(Root, "src/Deleted.cs", 3));
    }
}
