using System.Text;
using KyberWeave.Kernel.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Doctor;

/// <summary>
/// The contract every <see cref="IDoctorFileSystem"/> must satisfy. Run it once against the in-memory fake and once
/// against the adapter over the real disk, so a test that pins link handling or a read window proves both agree.
/// </summary>
/// <remarks>
/// A derived class creates the fixture entries itself, because the seam is read-only. The scratch directory is under
/// the system temporary directory, so a real-disk run leaves nothing behind.
/// </remarks>
public abstract class DoctorFileSystemContractTests : IDisposable
{
    /// <summary>The file system under test.</summary>
    protected IDoctorFileSystem Files { get; private set; } = null!;

    /// <summary>The scratch directory the fixture lives in.</summary>
    protected string Root { get; private set; } = string.Empty;

    /// <summary>Creates the file system under test.</summary>
    protected abstract IDoctorFileSystem CreateFileSystem();

    /// <summary>Creates one empty directory in the fixture.</summary>
    protected abstract void EnsureDirectory(string path);

    /// <summary>Writes one text file in the fixture.</summary>
    protected abstract void WriteFile(string path, string text);

    /// <summary>Creates a symbolic link at <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>.</summary>
    protected abstract void CreateLink(string linkPath, string targetPath);

    /// <summary>Removes the scratch directory, best effort.</summary>
    protected abstract void DeleteTree(string root);

    /// <summary>Starts one test: creates the file system and an empty scratch directory.</summary>
    protected void Start()
    {
        Files = CreateFileSystem();
        Root = Path.Combine(Path.GetTempPath(), "kw-doctor-contract-" + Guid.NewGuid().ToString("N"));
        EnsureDirectory(Root);
    }

    /// <summary>Builds a path inside the scratch directory.</summary>
    protected string At(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Removes the scratch directory, best effort.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                DeleteTree(Root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: the fixture sits under the system temporary directory.
            }
        }
    }

    [Fact]
    public void AMissingFileIsNeitherAFileNorAPresentFileAndCannotBeRead()
    {
        Start();
        string path = At("missing.json");

        Assert.False(Files.FileExists(path));
        Assert.False(Files.IsPresentFile(path));
        _ = Assert.Throws<FileNotFoundException>(() => Files.ReadAllBytes(path));
    }

    [Fact]
    public void AFileExistsAndReadsItsBytesAndItsText()
    {
        Start();
        string path = At("settings.json");
        WriteFile(path, "{\"a\":1}\n");

        Assert.True(Files.FileExists(path));
        Assert.True(Files.IsPresentFile(path));
        Assert.Equal("{\"a\":1}\n", Files.ReadAllText(path));
        Assert.Equal("{\"a\":1}\n", Encoding.UTF8.GetString(Files.ReadAllBytes(path)));
    }

    [Fact]
    public void ADirectoryIsADirectoryAndNeitherAFileNorAPresentFile()
    {
        Start();
        string directory = At("user-data");
        EnsureDirectory(directory);

        Assert.True(Files.DirectoryExists(directory));
        Assert.False(Files.FileExists(directory));
        Assert.False(Files.IsPresentFile(directory));
    }

    [Fact]
    public void ADanglingDirectoryHasNothingInIt()
    {
        Start();

        Assert.False(Files.DirectoryExists(At("missing-directory")));
        Assert.Empty(Files.ListEntries(At("missing-directory")));
    }

    [Fact]
    public void ALinkToAFileIsReadAsItsTarget()
    {
        Start();
        string target = At("target.json");
        WriteFile(target, "target");
        string link = At("settings.json");
        CreateLink(link, target);

        Assert.True(Files.FileExists(link));
        Assert.True(Files.IsPresentFile(link));
        Assert.Equal("target", Files.ReadAllText(link));
    }

    [Fact]
    public void ADanglingLinkIsAnEntryButNotAPresentFileAndCannotBeRead()
    {
        Start();
        string link = At("settings.json");
        CreateLink(link, At("missing.json"));

        // FileExists is the doctor's gate before it reads anything, so it is pinned: the adapter asks the runtime,
        // which reports an entry for a link whose target is missing. Whether there is a file to read is IsPresentFile.
        Assert.True(Files.FileExists(link));
        Assert.False(Files.IsPresentFile(link));
        _ = Assert.Throws<FileNotFoundException>(() => Files.ReadAllBytes(link));
    }

    [Fact]
    public void ALinkToADirectoryIsADirectoryAndNotAPresentFile()
    {
        Start();
        string directory = At("user-data");
        EnsureDirectory(directory);
        string link = At("settings-directory");
        CreateLink(link, directory);

        // The adapter asks a FileInfo to resolve the link, so a directory target has no file to read.
        Assert.True(Files.DirectoryExists(link));
        Assert.False(Files.IsPresentFile(link));
        Assert.False(Files.FileExists(link));
    }

    [Fact]
    public void ListEntriesNamesTheImmediateChildrenAndFlagsALink()
    {
        Start();
        WriteFile(At("settings.json"), "{}");
        EnsureDirectory(At("user-data"));
        string target = At("target.json");
        WriteFile(target, "x");
        CreateLink(At("linked.json"), target);
        WriteFile(At("user-data", "nested.json"), "{}");

        IReadOnlyList<DoctorDirectoryEntry> entries = Files.ListEntries(Root);

        Assert.Contains(entries, entry => entry.Name == "settings.json" && !entry.IsDirectory && !entry.IsLink);
        Assert.Contains(entries, entry => entry.Name == "user-data" && entry.IsDirectory && !entry.IsLink);
        Assert.Contains(entries, entry => entry.Name == "target.json" && !entry.IsDirectory && !entry.IsLink);
        Assert.Contains(entries, entry => entry.Name == "linked.json" && entry.IsLink);
        Assert.DoesNotContain(entries, entry => entry.Name == "nested.json");
    }

    [Fact]
    public void ReadTailReturnsTheLastWindowAndFlagsAPartialLine()
    {
        Start();
        string path = At("agent.log");
        WriteFile(path, "one\ntwo\nthree\n");

        string partial = Files.ReadTail(path, 5, out bool startsPartWay);

        Assert.True(startsPartWay);
        Assert.Equal("hree\n", partial);
    }

    [Fact]
    public void ReadTailReturnsTheLastWindowWithNoPartialFlagWhenItStartsOnALineBreak()
    {
        Start();
        string path = At("agent.log");
        WriteFile(path, "one\ntwo\nthree\n");

        string wholeLine = Files.ReadTail(path, 6, out bool startsPartWay);

        Assert.False(startsPartWay);
        Assert.Equal("three\n", wholeLine);
    }

    [Fact]
    public void ReadTailReturnsTheWholeFileWhenItFits()
    {
        Start();
        string path = At("agent.log");
        WriteFile(path, "one\ntwo\nthree\n");

        string whole = Files.ReadTail(path, 100, out bool startsPartWay);

        Assert.False(startsPartWay);
        Assert.Equal("one\ntwo\nthree\n", whole);
    }
}
