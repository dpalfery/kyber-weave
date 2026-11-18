using System.Text;
using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The contained file reader over the in-memory file system: which paths it accepts, and how a bounded read splits its
/// lines, are proven without a disk.
/// </summary>
public sealed class FileContainedFileReaderTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private readonly InMemoryFileSystem _files = new();
    private readonly FileContainedFileReader _reader;

    public FileContainedFileReaderTests()
    {
        _reader = new FileContainedFileReader(_files);
        _files.CreateDirectory(Path.Combine(Root, "src"));
        _files.CreateDirectory(Path.Combine(Root, "real"));
        _files.WriteAllText(Path.Combine(Root, "src", "a.cs"), "one\ntwo\nthree");
        _files.WriteAllText(Path.Combine(Root, "real", "b.cs"), "content");
    }

    [Fact]
    public void AFileInTheTreeWithNoLinkOnItsPathIsContained()
    {
        Assert.True(_reader.IsContainedFile(Root, "src/a.cs"));
    }

    [Fact]
    public void AMissingFileIsNotContained()
    {
        Assert.False(_reader.IsContainedFile(Root, "src/absent.cs"));
    }

    [Fact]
    public void AFileReachedThroughALinkIsNotContained()
    {
        _files.CreateFileLink(Path.Combine(Root, "linked.cs"), Path.Combine(Root, "real", "b.cs"));
        _files.CreateDirectoryLink(Path.Combine(Root, "alias"), Path.Combine(Root, "real"));

        Assert.False(_reader.IsContainedFile(Root, "linked.cs"));
        Assert.False(_reader.IsContainedFile(Root, "alias/b.cs"));
    }

    [Fact]
    public void ARepositoryRootIsNotAFile()
    {
        Assert.False(_reader.IsContainedFile(Root, "."));
    }

    [Fact]
    public void APathThatClimbsOutOfTheTreeIsNotContained()
    {
        _files.WriteAllText(Path.Combine(Root, "..", "outside.cs"), "outside");

        Assert.False(_reader.IsContainedFile(Root, "../outside.cs"));
        Assert.False(_reader.IsContainedFile(Root, "src/../../outside.cs"));
    }

    [Fact]
    public void ARootedPathIsNotAContainedFile()
    {
        Assert.False(_reader.IsContainedFile(Root, Path.Combine(Root, "src", "a.cs")));
    }

    [Fact]
    public void ReadLinesSplitsTheFileTheSameWayTheFileSystemDoes()
    {
        string path = Path.Combine(Root, "src", "a.cs");

        Assert.Equal(["one", "two", "three"], _reader.ReadLines(path, maxBytes: 1024));
    }

    [Fact]
    public void ReadLinesDropsTheByteOrderMarkAsTheReaderItSharesDoes()
    {
        string path = Path.Combine(Root, "src", "bom.cs");
        _files.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("alpha\nbeta")]);

        Assert.Equal(["alpha", "beta"], _reader.ReadLines(path, maxBytes: 1024));
    }

    [Fact]
    public void ReadLinesRefusesAFileOverTheBound()
    {
        string path = Path.Combine(Root, "src", "big.cs");
        _files.WriteAllText(path, "a line longer than four bytes");

        _ = Assert.Throws<InvalidDataException>(() => _reader.ReadLines(path, maxBytes: 4));
    }
}
