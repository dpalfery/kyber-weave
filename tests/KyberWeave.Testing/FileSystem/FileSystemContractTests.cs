using System.Text;
using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Tests.FileSystem;

/// <summary>
/// The contract every <see cref="IWritableFileSystem"/> must satisfy. Run it once against the in-memory fake and
/// once against the physical adapter, so a fake cannot keep a suite green while real files behave differently.
/// </summary>
/// <remarks>
/// A derived class supplies only the implementation under test. The suite creates its own scratch directories
/// through that implementation, so it works for both a fake and the real disk and leaves nothing behind.
/// </remarks>
public abstract class FileSystemContractTests : IDisposable
{
    private readonly List<(IWritableFileSystem Files, string Path)> _scratch = [];

    /// <summary>Creates the implementation under test.</summary>
    /// <returns>A writable file system. The suite may create more than one instance.</returns>
    protected abstract IWritableFileSystem CreateFileSystem();

    /// <summary>Creates the implementation under test and one empty scratch directory.</summary>
    /// <param name="root">The scratch directory the test writes under.</param>
    /// <returns>The implementation.</returns>
    protected IWritableFileSystem Starting(out string root)
    {
        IWritableFileSystem files = CreateFileSystem();
        root = NewScratch(files, "kw-fs-contract-root-");
        return files;
    }

    /// <summary>Creates a second empty scratch directory, outside the tree the test walks.</summary>
    /// <param name="files">The implementation under test.</param>
    /// <param name="prefix">The directory-name prefix.</param>
    /// <returns>The new directory.</returns>
    protected string AnotherDirectory(IWritableFileSystem files, string prefix) => NewScratch(files, prefix);

    /// <summary>Removes every scratch directory the test created.</summary>
    public void Dispose()
    {
        foreach ((IWritableFileSystem files, string path) in _scratch)
        {
            try
            {
                files.DeleteTreeNoFollow(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover sits under the system temporary directory.
            }
        }

        GC.SuppressFinalize(this);
    }

    private string NewScratch(IWritableFileSystem files, string prefix)
    {
        string path = files.CreateTemporaryDirectory(prefix);
        _scratch.Add((files, path));
        return path;
    }

    /// <summary>An absolute path under a directory.</summary>
    protected static string At(string directory, string relative) => Path.Combine(directory, relative);

    /// <summary>A path whose parent directory exists.</summary>
    protected static string WriteFile(IWritableFileSystem files, string directory, string relative, string text)
    {
        string path = At(directory, relative);
        string? parent = Path.GetDirectoryName(path);
        if (parent is not null)
        {
            files.CreateDirectory(parent);
        }

        files.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void ExistenceProbesTellFilesAndDirectoriesApart()
    {
        IWritableFileSystem files = Starting(out string root);
        string file = WriteFile(files, root, "present.txt", "x");
        string directory = At(root, "folder");
        files.CreateDirectory(directory);

        Assert.True(files.FileExists(file));
        Assert.False(files.DirectoryExists(file));
        Assert.True(files.DirectoryExists(directory));
        Assert.False(files.FileExists(directory));
        Assert.False(files.FileExists(At(root, "absent.txt")));
        Assert.False(files.DirectoryExists(At(root, "absent")));
    }

    [Fact]
    public void PathExistsCoversFilesAndDirectories()
    {
        IWritableFileSystem files = Starting(out string root);
        string file = WriteFile(files, root, "present.txt", "x");

        Assert.True(files.PathExists(root));
        Assert.True(files.PathExists(file));
        Assert.False(files.PathExists(At(root, "absent")));
    }

    [Fact]
    public void DeleteTreeNoFollowRemovesAFilePathAndToleratesAMissingOne()
    {
        IWritableFileSystem files = Starting(out string root);
        string file = WriteFile(files, root, "leftover.txt", "x");

        files.DeleteTreeNoFollow(file);
        files.DeleteTreeNoFollow(At(root, "absent"));

        Assert.False(files.FileExists(file));
    }

    [Fact]
    public void DeleteTreeNoFollowRemovesADanglingLinkItself()
    {
        IWritableFileSystem files = Starting(out string root);
        string dangling = At(root, "dangling");
        files.CreateDirectoryLink(dangling, At(root, "missing-target"));
        Assert.Contains(files.EnumerateEntries(root), entry => entry.Path == dangling && entry.IsLink);

        files.DeleteTreeNoFollow(dangling);

        Assert.DoesNotContain(files.EnumerateEntries(root), entry => entry.Path == dangling);
    }

    [Fact]
    public void CreateDirectoryCreatesMissingParents()
    {
        IWritableFileSystem files = Starting(out string root);
        string nested = At(root, "one/two/three");

        files.CreateDirectory(nested);
        files.CreateDirectory(nested);

        Assert.True(files.DirectoryExists(nested));
    }

    [Fact]
    public void ReadsOfAMissingFileFailAsDocumented()
    {
        IWritableFileSystem files = Starting(out string root);
        string missing = At(root, "absent.txt");

        _ = Assert.Throws<FileNotFoundException>(() => files.ReadAllText(missing));
        _ = Assert.Throws<FileNotFoundException>(() => files.ReadAllBytes(missing));
        _ = Assert.Throws<FileNotFoundException>(() => files.OpenRead(missing));
        _ = Assert.Throws<FileNotFoundException>(() => files.GetLength(missing));
        _ = Assert.Throws<FileNotFoundException>(() => files.GetAttributes(missing));
    }

    [Fact]
    public void ReadLinesDefersTheFailureUntilTheCallerEnumerates()
    {
        IWritableFileSystem files = Starting(out string root);
        IEnumerable<string> lines = files.ReadLines(At(root, "absent.txt"));

        _ = Assert.Throws<FileNotFoundException>(() => lines.ToList());
    }

    [Fact]
    public void ReadAllTextReturnsWhatWasWritten()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = At(root, "note.txt");
        files.CreateDirectory(root);

        files.WriteAllText(path, "first line\nsecond line");

        Assert.Equal("first line\nsecond line", files.ReadAllText(path));
    }

    [Fact]
    public void ReadAllBytesReturnsEveryByteValue()
    {
        IWritableFileSystem files = Starting(out string root);
        byte[] bytes = [0, 1, 127, 128, 255, 10];
        string path = At(root, "raw.bin");

        files.WriteAllBytes(path, bytes);

        Assert.Equal(bytes, files.ReadAllBytes(path));
    }

    [Fact]
    public void ReadLinesSplitsOnThePlatformLineBreaks()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = WriteFile(files, root, "lines.txt", "alpha\nbeta\ngamma\n");

        Assert.Equal(["alpha", "beta", "gamma"], files.ReadLines(path));
    }

    [Fact]
    public void OpenReadReturnsTheFileContent()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = WriteFile(files, root, "stream.txt", "streamed");

        using Stream stream = files.OpenRead(path);
        using StreamReader reader = new(stream, Encoding.UTF8);

        Assert.Equal("streamed", reader.ReadToEnd());
    }

    [Fact]
    public void LengthAndLastWriteTimeDescribeTheFile()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = WriteFile(files, root, "sized.txt", "12345");
        DateTime before = DateTime.UtcNow.AddMinutes(-5);

        Assert.Equal(5, files.GetLength(path));
        Assert.InRange(files.GetLastWriteTimeUtc(path), before, DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public void AttributesMarkAFolderAndLeaveAFileUnmarked()
    {
        IWritableFileSystem files = Starting(out string root);
        string file = WriteFile(files, root, "plain.txt", "x");
        string directory = At(root, "folder");
        files.CreateDirectory(directory);

        Assert.Equal(FileAttributes.Directory, files.GetAttributes(directory) & FileAttributes.Directory);
        Assert.Equal(0, (int)(files.GetAttributes(file) & FileAttributes.Directory));
    }

    [Fact]
    public void EnumerateFilesFindsDirectAndNestedFiles()
    {
        IWritableFileSystem files = Starting(out string root);
        _ = WriteFile(files, root, "top.txt", "x");
        _ = WriteFile(files, root, "nested/deep.txt", "x");
        _ = WriteFile(files, root, "nested/other.md", "x");

        Assert.Equal([At(root, "top.txt")], files.EnumerateFiles(root, "*.txt", recursive: false));
        Assert.Equal(
            [At(root, "nested/deep.txt"), At(root, "top.txt")],
            files.EnumerateFiles(root, "*.txt", recursive: true).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EnumerateFilesWithoutFollowingLinksStaysInsideTheTree()
    {
        IWritableFileSystem files = Starting(out string root);
        string outside = AnotherDirectory(files, "kw-fs-contract-outside-");
        _ = WriteFile(files, root, "inside.txt", "x");
        _ = WriteFile(files, outside, "linked.txt", "x");
        files.CreateDirectoryLink(At(root, "linked"), outside);

        Assert.Equal(
            [At(root, "inside.txt")],
            files.EnumerateFiles(root, "*.txt", recursive: true, followLinks: false));

        Assert.Equal(
            [At(root, "inside.txt"), At(root, "linked/linked.txt")],
            files.EnumerateFiles(root, "*.txt", recursive: true, followLinks: true).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EnumerateDirectoriesListsImmediateChildren()
    {
        IWritableFileSystem files = Starting(out string root);
        files.CreateDirectory(At(root, "one"));
        files.CreateDirectory(At(root, "two"));
        _ = WriteFile(files, root, "one/nested/inner.txt", "x");

        Assert.Equal([At(root, "one"), At(root, "two")], files.EnumerateDirectories(root).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EnumerateEntriesReportsKindAndLink()
    {
        IWritableFileSystem files = Starting(out string root);
        _ = WriteFile(files, root, "plain.txt", "x");
        files.CreateDirectory(At(root, "folder"));
        files.CreateFileLink(At(root, "linked.txt"), At(root, "plain.txt"));

        IReadOnlyList<FileSystemEntry> entries = [.. files.EnumerateEntries(root)];

        FileSystemEntry plain = Assert.Single(entries, entry => entry.Path == At(root, "plain.txt"));
        Assert.False(plain.IsDirectory);
        Assert.False(plain.IsLink);

        FileSystemEntry folder = Assert.Single(entries, entry => entry.Path == At(root, "folder"));
        Assert.True(folder.IsDirectory);
        Assert.False(folder.IsLink);

        FileSystemEntry link = Assert.Single(entries, entry => entry.Path == At(root, "linked.txt"));
        Assert.True(link.IsLink);
    }

    [Fact]
    public void GetLinkTargetReportsTheTargetAsWritten()
    {
        IWritableFileSystem files = Starting(out string root);
        string target = WriteFile(files, root, "target.txt", "x");
        string link = At(root, "link.txt");
        files.CreateFileLink(link, target);

        string? written = files.GetLinkTarget(link);

        Assert.NotNull(written);
        Assert.Equal(target, Path.GetFullPath(written, root));
        Assert.Null(files.GetLinkTarget(target));
        Assert.Null(files.GetLinkTarget(At(root, "absent.txt")));
    }

    [Fact]
    public void GetFinalLinkTargetResolvesEveryHop()
    {
        IWritableFileSystem files = Starting(out string root);
        string target = WriteFile(files, root, "target.txt", "x");
        string first = At(root, "first.txt");
        string second = At(root, "second.txt");
        files.CreateFileLink(first, target);
        files.CreateFileLink(second, first);

        Assert.Equal(target, files.GetFinalLinkTarget(first));
        Assert.Equal(target, files.GetFinalLinkTarget(second));
        Assert.Null(files.GetFinalLinkTarget(target));
    }

    [Fact]
    public void GetFinalLinkTargetOfADanglingLinkIsNull()
    {
        IWritableFileSystem files = Starting(out string root);
        string dangling = At(root, "dangling.txt");
        files.CreateFileLink(dangling, At(root, "missing-target.txt"));

        Assert.Null(files.GetFinalLinkTarget(dangling));
    }

    [Fact]
    public void WriteAllTextReplacesAnEarlierFile()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = WriteFile(files, root, "replace.txt", "longer content");

        files.WriteAllText(path, "short");

        Assert.Equal("short", files.ReadAllText(path));
    }

    [Fact]
    public void AtomicTextWriteReplacesTheFileAndLeavesNoTemporary()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = WriteFile(files, root, "atomic.txt", "old");

        files.WriteAllTextAtomic(path, "new");

        Assert.Equal("new", files.ReadAllText(path));
        Assert.Equal([At(root, "atomic.txt")], files.EnumerateFiles(root, "*", recursive: false));
    }

    [Fact]
    public void AtomicTextWriteCreatesTheDirectoryItNeeds()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = At(root, "missing/nested/atomic.txt");

        files.WriteAllTextAtomic(path, "created");

        Assert.Equal("created", files.ReadAllText(path));
    }

    [Fact]
    public void AtomicByteWriteReplacesTheFile()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = At(root, "atomic.bin");
        files.WriteAllBytes(path, [9, 9, 9]);

        files.WriteAllBytesAtomic(path, [1, 2]);

        Assert.Equal([1, 2], files.ReadAllBytes(path));
    }

    [Fact]
    public void MoveFileWithoutOverwriteRefusesAnOccupiedDestination()
    {
        IWritableFileSystem files = Starting(out string root);
        string source = WriteFile(files, root, "source.txt", "source");
        string destination = WriteFile(files, root, "destination.txt", "destination");

        _ = Assert.Throws<IOException>(() => files.MoveFile(source, destination));

        Assert.Equal("source", files.ReadAllText(source));
        Assert.Equal("destination", files.ReadAllText(destination));
    }

    [Fact]
    public void MoveFileWithoutOverwriteMovesToAFreePath()
    {
        IWritableFileSystem files = Starting(out string root);
        string source = WriteFile(files, root, "source.txt", "moved");

        files.MoveFile(source, At(root, "destination.txt"));

        Assert.False(files.FileExists(source));
        Assert.Equal("moved", files.ReadAllText(At(root, "destination.txt")));
    }

    [Fact]
    public void MoveFileWithOverwriteReplacesTheDestination()
    {
        IWritableFileSystem files = Starting(out string root);
        string source = WriteFile(files, root, "source.txt", "new");
        string destination = WriteFile(files, root, "destination.txt", "old");

        files.MoveFile(source, destination, overwrite: true);

        Assert.False(files.FileExists(source));
        Assert.Equal("new", files.ReadAllText(destination));
    }

    [Fact]
    public void CreateNewFileRefusesAnOccupiedPath()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = WriteFile(files, root, "taken.txt", "taken");

        _ = Assert.Throws<IOException>(() => files.CreateNewFile(path));

        Assert.Equal("taken", files.ReadAllText(path));
    }

    [Fact]
    public void CreateNewFileCreatesAnEmptyFile()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = At(root, "fresh.txt");

        files.CreateNewFile(path);

        Assert.True(files.FileExists(path));
        Assert.Equal(0, files.GetLength(path));
    }

    [Fact]
    public void TryCreateNewFileReportsALostRaceToADirectoryWithoutThrowing()
    {
        IWritableFileSystem files = Starting(out string root);
        string directory = At(root, "occupied");
        files.CreateDirectory(directory);

        Assert.False(files.TryCreateNewFile(directory));
        Assert.True(files.DirectoryExists(directory));
    }

    [Fact]
    public void TryCreateNewFileReportsALostRaceWithoutThrowing()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = At(root, "claim.txt");

        Assert.True(files.TryCreateNewFile(path));
        Assert.False(files.TryCreateNewFile(path));
        Assert.Equal(0, files.GetLength(path));
    }

    [Fact]
    public void DeleteFileRemovesItAndToleratesAMissingOne()
    {
        IWritableFileSystem files = Starting(out string root);
        string path = WriteFile(files, root, "gone.txt", "x");

        files.DeleteFile(path);
        files.DeleteFile(path);

        Assert.False(files.FileExists(path));
    }

    [Fact]
    public void DeleteTreeNoFollowRemovesTheTreeAndKeepsTheLinkTarget()
    {
        IWritableFileSystem files = Starting(out string root);
        string outside = AnotherDirectory(files, "kw-fs-contract-outside-");
        string kept = WriteFile(files, outside, "kept.txt", "keep me");
        string tree = At(root, "tree");
        _ = WriteFile(files, tree, "nested/file.txt", "x");
        files.CreateDirectoryLink(At(tree, "link"), outside);
        files.CreateFileLink(At(tree, "linked-file.txt"), kept);

        files.DeleteTreeNoFollow(tree);
        files.DeleteTreeNoFollow(tree);

        Assert.False(files.DirectoryExists(tree));
        Assert.True(files.FileExists(kept));
        Assert.Equal("keep me", files.ReadAllText(kept));
    }

    [Fact]
    public void CreateDirectoryLinkAndFileLinkCreateLinks()
    {
        IWritableFileSystem files = Starting(out string root);
        string directory = At(root, "target-folder");
        files.CreateDirectory(directory);
        string target = WriteFile(files, root, "target.txt", "x");
        string directoryLink = At(root, "folder-link");
        string fileLink = At(root, "file-link.txt");

        files.CreateDirectoryLink(directoryLink, directory);
        files.CreateFileLink(fileLink, target);

        Assert.True(files.DirectoryExists(directoryLink));
        Assert.NotNull(files.GetLinkTarget(directoryLink));
        Assert.Equal(target, files.GetFinalLinkTarget(fileLink));
        Assert.Equal("x", files.ReadAllText(fileLink));
    }

    [Fact]
    public void CreateTemporaryDirectoryRejectsAPrefixThatLeavesTheTemporaryRoot()
    {
        IWritableFileSystem files = Starting(out string root);

        _ = Assert.Throws<ArgumentException>(() => files.CreateTemporaryDirectory(root));
        _ = Assert.Throws<ArgumentException>(() => files.CreateTemporaryDirectory("nested/prefix-"));
        _ = Assert.Throws<ArgumentException>(() => files.CreateTemporaryDirectory(string.Empty));
    }

    [Fact]
    public void CreateTemporaryDirectoryCreatesAnEmptyUniqueDirectory()
    {
        IWritableFileSystem files = CreateFileSystem();

        string first = files.CreateTemporaryDirectory("kw-fs-contract-temp-");
        string second = files.CreateTemporaryDirectory("kw-fs-contract-temp-");
        _scratch.Add((files, first));
        _scratch.Add((files, second));

        Assert.NotEqual(first, second);
        Assert.True(files.DirectoryExists(first));
        Assert.True(files.DirectoryExists(second));
        Assert.Empty(files.EnumerateEntries(first));
        Assert.Empty(files.EnumerateEntries(second));
    }
}
