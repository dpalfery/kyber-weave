using KyberWeave.Data.Review;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The cited source files over the in-memory file system: which of a seat's written names becomes a path, and which lines
/// a window returns, are proven without a disk.
/// </summary>
public sealed class FileCitedSourceFilesTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private readonly InMemoryFileSystem _files = new();
    private readonly FileCitedSourceFiles _sources;

    public FileCitedSourceFilesTests()
    {
        _sources = new FileCitedSourceFiles(_files, new FileContainedFileReader(_files));
        _files.CreateDirectory(Path.Combine(Root, "src"));
        _files.WriteAllText(Path.Combine(Root, "src", "a.cs"), "one\ntwo\nthree\nfour");
    }

    [Fact]
    public void ResolveReturnsTheFullPathOfAContainedFileUnderTheBound()
    {
        Assert.Equal(
            Path.Combine(Root, "src", "a.cs"),
            _sources.Resolve(Root, "src/a.cs", maxBytes: 1024));
    }

    [Fact]
    public void ResolveRefusesAFileOverTheBound()
    {
        Assert.Null(_sources.Resolve(Root, "src/a.cs", maxBytes: 4));
    }

    [Fact]
    public void ResolveRefusesAMissingFile()
    {
        Assert.Null(_sources.Resolve(Root, "src/absent.cs", maxBytes: 1024));
    }

    [Fact]
    public void ResolveRefusesANameThatLeavesTheRepository()
    {
        _files.WriteAllText(Path.Combine(Root, "..", "outside.cs"), "outside");
        _files.CreateDirectory(Root + "2");
        _files.WriteAllText(Path.Combine(Root + "2", "sibling.cs"), "sibling");
        _files.CreateFileLink(Path.Combine(Root, "linked.cs"), Path.Combine(Root, "src", "a.cs"));

        Assert.Null(_sources.Resolve(Root, "../outside.cs", maxBytes: 1024));
        Assert.Null(_sources.Resolve(Root, "../repo2/sibling.cs", maxBytes: 1024));
        Assert.Null(_sources.Resolve(Root, "linked.cs", maxBytes: 1024));
    }

    [Fact]
    public void ReadWindowReturnsTheRequestedLinesAndStopsAtTheEndOfTheFile()
    {
        string path = Path.Combine(Root, "src", "a.cs");

        Assert.Equal(["two", "three"], _sources.ReadWindow(path, firstLine: 2, count: 2));
        Assert.Equal(["four"], _sources.ReadWindow(path, firstLine: 4, count: 10));
    }

    [Fact]
    public void ReadWindowBeyondTheLastIndexableLineReturnsNoLines()
    {
        string path = Path.Combine(Root, "src", "a.cs");

        Assert.Empty(_sources.ReadWindow(path, firstLine: (long)int.MaxValue + 2, count: 1));
    }
}
