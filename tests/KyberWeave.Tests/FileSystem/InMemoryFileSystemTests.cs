using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Tests.FileSystem;

/// <summary>Runs the file-system contract against the in-memory fake, once per case rule it offers.</summary>
public abstract class InMemoryFileSystemContractTests : FileSystemContractTests
{
    /// <inheritdoc />
    protected override IWritableFileSystem CreateFileSystem() => new InMemoryFileSystem(caseSensitive: CaseSensitive);
}

/// <summary>Runs the file-system contract against the fake with APFS and NTFS name rules.</summary>
public sealed class InMemoryFileSystemCaseInsensitiveContractTests : InMemoryFileSystemContractTests
{
    /// <inheritdoc />
    protected override bool CaseSensitive => false;
}

/// <summary>Runs the file-system contract against the fake with Linux name rules.</summary>
public sealed class InMemoryFileSystemCaseSensitiveContractTests : InMemoryFileSystemContractTests
{
    /// <inheritdoc />
    protected override bool CaseSensitive => true;
}

/// <summary>Runs the fault-injection contract against the in-memory fake.</summary>
public sealed class InMemoryFileSystemFaultContractTests : FileSystemFaultContractTests
{
    /// <inheritdoc />
    protected override InMemoryFileSystem CreateFileSystem() => new();
}

/// <summary>Proves the fake semantics a shared contract cannot state: exact timestamps, stored attributes and case preservation.</summary>
public sealed class InMemoryFileSystemBehaviourTests
{
    private static string At(string directory, string relative) => Path.Combine(directory, relative);

    [Fact]
    public void LastWriteTimesComeFromTheInjectedClock()
    {
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero));
        InMemoryFileSystem files = new(clock);
        string root = files.CreateTemporaryDirectory("kw-memfs-time-");
        string path = At(root, "stamped.txt");

        files.WriteAllText(path, "first");
        Assert.Equal(clock.GetUtcNow().UtcDateTime, files.GetLastWriteTimeUtc(path));

        clock.Advance(TimeSpan.FromMinutes(5));
        files.WriteAllTextAtomic(path, "second");

        Assert.Equal(clock.GetUtcNow().UtcDateTime, files.GetLastWriteTimeUtc(path));
        Assert.Equal("second", files.ReadAllText(path));
    }

    [Fact]
    public void CaseInsensitiveLookupCoversEveryPathSegmentAndKeepsTheStoredNames()
    {
        InMemoryFileSystem files = new(caseSensitive: false);
        string root = files.CreateTemporaryDirectory("kw-memfs-case-");
        string path = At(root, "Folder/Name.txt");
        files.CreateDirectory(At(root, "Folder"));
        files.WriteAllText(path, "kept");

        Assert.True(files.FileExists(At(root, "folder/name.txt")));
        Assert.Equal("kept", files.ReadAllText(At(root, "FOLDER/NAME.TXT")));
        Assert.Equal([At(root, "Folder")], files.EnumerateEntries(root).Select(entry => entry.Path));
        Assert.Equal([At(root, "folder/Name.txt")], files.EnumerateEntries(At(root, "folder")).Select(entry => entry.Path));
    }

    [Fact]
    public void CaseSensitiveLookupTreatsEverySpellingAsItsOwnName()
    {
        InMemoryFileSystem files = new(caseSensitive: true);
        string root = files.CreateTemporaryDirectory("kw-memfs-case-");
        files.WriteAllText(At(root, "Name.txt"), "one");

        Assert.False(files.FileExists(At(root, "name.txt")));

        files.WriteAllText(At(root, "name.txt"), "two");

        Assert.Equal("one", files.ReadAllText(At(root, "Name.txt")));
        Assert.Equal("two", files.ReadAllText(At(root, "name.txt")));
        Assert.Equal(2, files.EnumerateEntries(root).Count());
    }

    [Fact]
    public void AskingAnExistingDirectoryForExactNamesChangesHowItMatchesThem()
    {
        InMemoryFileSystem files = new(caseSensitive: false);
        string root = files.CreateTemporaryDirectory("kw-memfs-exact-");
        string folder = At(root, "Folder");
        files.CreateDirectory(folder);
        files.WriteAllText(At(folder, "Name.txt"), "one");

        files.CreateDirectory(folder, caseSensitiveNames: true);
        files.WriteAllText(At(folder, "name.txt"), "two");

        Assert.Equal(2, files.EnumerateEntries(folder).Count());
        Assert.Equal("one", files.ReadAllText(At(folder, "Name.txt")));
        Assert.Equal("two", files.ReadAllText(At(folder, "name.txt")));
        Assert.False(files.FileExists(At(folder, "NAME.TXT")));
    }

    [Fact]
    public void AskingAnExistingDirectoryForFoldedNamesRefusesNamesThatWouldCollide()
    {
        InMemoryFileSystem files = new(caseSensitive: false);
        string root = files.CreateTemporaryDirectory("kw-memfs-exact-");
        files.CreateDirectory(root, caseSensitiveNames: true);
        files.WriteAllText(At(root, "Name.txt"), "one");
        files.WriteAllText(At(root, "name.txt"), "two");

        _ = Assert.Throws<IOException>(() => files.CreateDirectory(root, caseSensitiveNames: false));

        Assert.Equal("one", files.ReadAllText(At(root, "Name.txt")));
        Assert.Equal("two", files.ReadAllText(At(root, "name.txt")));
    }

    [Fact]
    public void CreatingAnExistingDirectoryAgainKeepsTheNamesItMatches()
    {
        InMemoryFileSystem files = new(caseSensitive: false);
        string root = files.CreateTemporaryDirectory("kw-memfs-exact-");
        files.CreateDirectory(root, caseSensitiveNames: true);
        files.WriteAllText(At(root, "Name.txt"), "one");

        files.CreateDirectory(root);

        Assert.False(files.FileExists(At(root, "NAME.TXT")));
    }

    [Fact]
    public void ARenameTakesTheDestinationSpelling()
    {
        InMemoryFileSystem files = new(caseSensitive: false);
        string root = files.CreateTemporaryDirectory("kw-memfs-rename-");
        string path = At(root, "Lower.txt");
        files.WriteAllText(path, "x");

        files.MoveFile(path, At(root, "LOWER.txt"));

        Assert.Equal([At(root, "LOWER.txt")], files.EnumerateEntries(root).Select(entry => entry.Path));
        Assert.Equal("x", files.ReadAllText(At(root, "lower.txt")));
    }

    [Fact]
    public void AReadOnlyFileIsRemovedWithItsTree()
    {
        InMemoryFileSystem files = new();
        string root = files.CreateTemporaryDirectory("kw-memfs-readonly-");
        string path = At(root, "locked.txt");
        files.WriteAllText(path, "x");
        files.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Equal(FileAttributes.ReadOnly, files.GetAttributes(path));

        files.DeleteTreeNoFollow(root);

        Assert.False(files.DirectoryExists(root));
    }

    [Fact]
    public void WritingToAReadOnlyFileIsRefused()
    {
        InMemoryFileSystem files = new();
        string root = files.CreateTemporaryDirectory("kw-memfs-readonly-");
        string path = At(root, "locked.txt");
        files.WriteAllText(path, "old");
        files.SetAttributes(path, FileAttributes.ReadOnly);

        _ = Assert.Throws<UnauthorizedAccessException>(() => files.WriteAllText(path, "new"));
        _ = Assert.Throws<UnauthorizedAccessException>(() => files.WriteAllBytes(path, [1]));

        Assert.Equal("old", files.ReadAllText(path));
        Assert.Equal(FileAttributes.ReadOnly, files.GetAttributes(path));
    }

    [Fact]
    public void TemporaryDirectoriesStayInsideTheVirtualRoot()
    {
        InMemoryFileSystem files = new();

        string first = files.CreateTemporaryDirectory("kw-memfs-temp-");

        Assert.True(Path.IsPathRooted(first));
        Assert.Equal("kw-memfs-tmp", Path.GetFileName(Path.GetDirectoryName(first)));
    }

    [Fact]
    public void ALinkToAFileIsReportedAsAFileAndALinkToADirectoryAsADirectory()
    {
        InMemoryFileSystem files = new();
        string root = files.CreateTemporaryDirectory("kw-memfs-link-");
        string file = At(root, "target.txt");
        string directory = At(root, "target-folder");
        files.WriteAllText(file, "x");
        files.CreateDirectory(directory);
        files.CreateFileLink(At(root, "file-link.txt"), file);
        files.CreateDirectoryLink(At(root, "folder-link"), directory);

        Assert.True(files.FileExists(At(root, "file-link.txt")));
        Assert.False(files.DirectoryExists(At(root, "file-link.txt")));
        Assert.True(files.DirectoryExists(At(root, "folder-link")));
        Assert.False(files.FileExists(At(root, "folder-link")));
        Assert.Equal("x", files.ReadAllText(At(root, "file-link.txt")));
    }
}
