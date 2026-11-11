using System.Text;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.13 (bot comment triage): <c>FileTextFacts.ReadExcerpt</c> reads the current text around the
/// line a review comment cites, so the provider judges the code as it is now rather than as the bot saw it. The read
/// is bounded, and it never leaves the repository root.
/// RED: <c>KyberWeave.Core.Arbiter.Facts.FileTextFacts</c> and <c>FileExcerpt</c> do not exist yet.
/// </summary>
/// <remarks>
/// Names (GREEN must match): <c>FileTextFacts.ReadExcerpt(string repoRoot, string path, int line)</c> returns
/// <c>FileExcerpt(string Path, int StartLine, int EndLine, string Text, bool Truncated)</c> or <c>null</c> when the
/// file is absent or outside the root; <c>FileTextFacts.MaxExcerptCharacters</c> bounds <c>Text</c>.
/// </remarks>
public sealed class FileTextFactsTests : IDisposable
{
    private readonly TempDirectory _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Root => Path.Combine(_sandbox.Path, "repo");

    private string WriteRepoFile(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
        return relativePath;
    }

    /// <summary>The excerpt is a window around the cited line, so the cited code is in it and the whole file is not.</summary>
    [Fact]
    public void TheExcerptWindowContainsTheCitedLineAndNotTheWholeFile()
    {
        string content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));
        string path = WriteRepoFile("src/Widget.cs", content);

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(FileTextFacts.ReadExcerpt(Root, path, 50));

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

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(FileTextFacts.ReadExcerpt(Root, path, 2));

        Assert.True(excerpt.Text.Length <= FileTextFacts.MaxExcerptCharacters, excerpt.Text.Length.ToString());
        Assert.True(excerpt.Truncated);
    }

    /// <summary>
    /// A comment cites a path, and a path that climbs out of the root names a file the repository does not own. The
    /// excerpt is not read, and the caller treats the comment as unsure.
    /// </summary>
    [Fact]
    public void APathThatLeavesTheRootIsNotRead()
    {
        File.WriteAllText(Path.Combine(_sandbox.Path, "outside.txt"), "secret\n");
        Directory.CreateDirectory(Root);

        Assert.Null(FileTextFacts.ReadExcerpt(Root, "../outside.txt", 1));
        Assert.Null(FileTextFacts.ReadExcerpt(Root, Path.Combine(_sandbox.Path, "outside.txt"), 1));
    }

    /// <summary>A file the branch has since deleted has no current text to judge a comment against.</summary>
    [Fact]
    public void AMissingCitedFileHasNoExcerpt()
    {
        Directory.CreateDirectory(Root);

        Assert.Null(FileTextFacts.ReadExcerpt(Root, "src/Deleted.cs", 3));
    }
}
