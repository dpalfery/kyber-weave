using System.Text;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The excerpt reader over the repository file-store seam: every file lives in memory, so the window, the bounds
/// and the containment rules are proven without a disk.
/// </summary>
public sealed class FileTextFactsOverSeamsTests
{
    private const string Root = "/repo";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/kw/work");

    public FileTextFactsOverSeamsTests() => _paths = new FakePathResolver(_files);

    private FileTextFacts Facts() => new(new InMemoryRepositoryFileStore(_files), _paths, _environment);

    private void Write(string relative, string content)
    {
        string full = Path.Combine(Root, relative);
        _files.CreateDirectory(Path.GetDirectoryName(full)!);
        _files.WriteAllText(full, content);
    }

    [Fact]
    public void TheExcerptIsTheWindowAroundTheCitedLine()
    {
        Write("src/Widget.cs", string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}")));

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(Facts().ReadExcerpt(Root, "src/Widget.cs", 50));

        Assert.Equal(40, excerpt.StartLine);
        Assert.Equal(60, excerpt.EndLine);
        Assert.StartsWith("line 40\n", excerpt.Text, StringComparison.Ordinal);
        Assert.EndsWith("line 60", excerpt.Text, StringComparison.Ordinal);
        Assert.False(excerpt.Truncated);
    }

    [Fact]
    public void ARelativeRepositoryRootResolvesAgainstTheInjectedCurrentDirectory()
    {
        _files.CreateDirectory("/kw/work/repo/src");
        _files.WriteAllText("/kw/work/repo/src/Widget.cs", "one\ntwo\nthree");

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(Facts().ReadExcerpt("repo", "src/Widget.cs", 2));

        Assert.Equal("one\ntwo\nthree", excerpt.Text);
    }

    [Fact]
    public void ALineNearTheStartKeepsTheWindowAtLineOne()
    {
        Write("a.txt", "one\ntwo\nthree");

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(Facts().ReadExcerpt(Root, "a.txt", 2));

        Assert.Equal(1, excerpt.StartLine);
        Assert.Equal(3, excerpt.EndLine);
        Assert.Equal("one\ntwo\nthree", excerpt.Text);
    }

    [Fact]
    public void ALongLineIsClippedAndMarkedTruncated()
    {
        Write("big.txt", "head\n" + new string('x', 5000) + "\ntail");

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(Facts().ReadExcerpt(Root, "big.txt", 2));

        Assert.True(excerpt.Truncated);
        Assert.True(excerpt.Text.Length <= FileTextFacts.MaxExcerptCharacters);
        Assert.DoesNotContain(new string('x', FileTextFacts.MaxLineCharacters + 1), excerpt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExcerptStopsAtTheCharacterCap()
    {
        Write("wide.txt", string.Join("\n", Enumerable.Range(0, 21).Select(_ => new string('y', 400))));

        FileExcerpt excerpt = Assert.IsType<FileExcerpt>(Facts().ReadExcerpt(Root, "wide.txt", 11));

        Assert.True(excerpt.Truncated);
        Assert.Equal(FileTextFacts.MaxExcerptCharacters, excerpt.Text.Length);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/elsewhere/outside.txt")]
    public void APathThatLeavesTheRootIsNeverRead(string path)
    {
        _files.CreateDirectory("/elsewhere");
        _files.WriteAllText("/elsewhere/outside.txt", "secret");
        _files.WriteAllText("/outside.txt", "secret");

        Assert.Null(Facts().ReadExcerpt(Root, path, 1));
    }

    [Fact]
    public void AFileReachedThroughALinkedFolderIsNeverRead()
    {
        _files.CreateDirectory("/elsewhere");
        _files.WriteAllText("/elsewhere/leak.txt", "secret");
        _files.CreateDirectory(Root);
        _files.CreateDirectoryLink("/repo/docs", "/elsewhere");

        Assert.Null(Facts().ReadExcerpt(Root, "docs/leak.txt", 1));
    }

    [Fact]
    public void TheContainmentGateRefusesAPathReachedThroughALink()
    {
        _files.CreateDirectory("/elsewhere");
        _files.WriteAllText("/elsewhere/leak.txt", "secret");
        _files.CreateDirectory(Root);
        _files.CreateDirectoryLink("/repo/docs", "/elsewhere");

        Assert.Null(Facts().ResolveWithinRoot(Root, "docs/leak.txt"));
        Assert.Null(Facts().ResolveContainedFile(Root, "docs/leak.txt"));
    }

    [Fact]
    public void TheContainmentGateStillAnswersAPlainPathUnderTheRoot()
    {
        Write("src/Widget.cs", "one");

        Assert.Equal("/repo/src/Widget.cs", Facts().ResolveWithinRoot(Root, "src/Widget.cs"));
    }

    [Fact]
    public void AMalformedCitedPathIsRefusedRatherThanThrown()
    {
        Assert.Null(Facts().ResolveWithinRoot(Root, "a\0b"));
        Assert.Null(Facts().ResolveContainedFile(Root, "a\0b"));
        Assert.Null(Facts().ReadExcerpt(Root, "a\0b", 1));
    }

    [Fact]
    public void AMissingFileAndALinePastTheEndReadAsAbsent()
    {
        Write("short.txt", "one\ntwo");

        Assert.Null(Facts().ReadExcerpt(Root, "nope.txt", 1));
        Assert.Null(Facts().ReadExcerpt(Root, "short.txt", 50));
        Assert.Null(Facts().ReadExcerpt(Root, "short.txt", 0));
    }

    [Fact]
    public void AFileThatCannotBeOpenedReadsAsAbsentNotAsAFailure()
    {
        Write("locked.txt", "one\ntwo");
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.OpenRead), new IOException("locked"));

        Assert.Null(Facts().ReadExcerpt(Root, "locked.txt", 1));
    }

    [Fact]
    public void ALengthComesFromTheFileSystem()
    {
        Write("n.txt", "abcd");

        Assert.Equal(Encoding.UTF8.GetByteCount("abcd"), Facts().LengthOf(Path.Combine(Root, "n.txt")));
    }
}
