using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Sections;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The excerpt read is bounded by the characters it consumes, skipped lines included. Failure this guards: a single
/// minified or generated line far past the cap is read to its newline, so one huge file costs a full read on every
/// comment. A file whose cited line sits near the top is not penalised for the size of its tail.
/// </summary>
public sealed class FileTextFactsByteCapTests
{
    private const int TailCharacters = 2 * 1024 * 1024;

    private readonly InMemoryFileSystem _files = new();

    private static string Root { get; } = ArbiterTestRoots.Under("repo");

    private string WriteRepoFile(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath);
        _files.CreateDirectory(Path.GetDirectoryName(full)!);
        _files.WriteAllText(full, content);
        return relativePath;
    }

    [Fact]
    public void ALineThatRunsPastTheCapIsAbsent()
    {
        string content = "// header\n" + new string('x', (int)SectionSelector.MaxDocumentBytes + 1) + "\n";
        string path = WriteRepoFile("src/Minified.js", content);

        Assert.Null(InMemoryFileTextFacts.Create(_files).ReadExcerpt(Root, path, 2));
    }

    [Fact]
    public void ACitedLineNearTheTopOfALargeFileStillReadsAnExcerpt()
    {
        string head = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}"));
        string path = WriteRepoFile("src/Large.cs", head + "\n" + new string('x', TailCharacters));

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(InMemoryFileTextFacts.Create(_files).ReadExcerpt(Root, path, 2));

        Assert.Contains("line 2", excerpt.Text, StringComparison.Ordinal);
    }
}
