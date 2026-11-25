using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The write half of the contract every <see cref="ISquadFileAccess"/> must satisfy. Run it once against the
/// in-memory fake and once against the physical adapter, so a fake cannot keep a suite green while real files
/// behave differently.
/// </summary>
/// <remarks>
/// A derived class supplies only the implementation under test. The suite asks that implementation for a scratch
/// directory path that does not exist yet, so the first write also proves a missing parent is created, and it
/// removes the directory again when the test ends.
/// </remarks>
public abstract class SquadFileAccessWriteContract : IDisposable
{
    private readonly List<(ISquadFileAccess Files, string Path)> _scratch = [];

    /// <summary>Creates the implementation under test.</summary>
    /// <returns>A Squad file seam. The suite may create more than one instance.</returns>
    protected abstract ISquadFileAccess CreateFileAccess();

    /// <summary>Creates the implementation under test and the path of a scratch directory that does not exist yet.</summary>
    /// <param name="root">The scratch directory the test writes under; the test's first write creates it.</param>
    /// <returns>The implementation.</returns>
    protected ISquadFileAccess Starting(out string root)
    {
        ISquadFileAccess files = CreateFileAccess();
        root = files.NewScratchDirectoryPath("kw-squad-write-contract-");
        _scratch.Add((files, root));
        return files;
    }

    /// <summary>Removes every scratch directory the test created.</summary>
    public void Dispose()
    {
        foreach ((ISquadFileAccess files, string path) in _scratch)
        {
            try
            {
                if (files.DirectoryExists(path))
                    files.DeleteDirectory(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover sits under the system temporary directory.
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A write to a path whose parent directories do not exist creates them and the file.</summary>
    [Fact]
    public void AWriteCreatesAMissingParentDirectoryAndTheFile()
    {
        ISquadFileAccess files = Starting(out string root);
        string parent = Path.Combine(root, "nested", "config");
        string path = Path.Combine(parent, "settings.json");

        files.WriteAllBytesAtomic(path, [0x7B, 0x7D]);

        Assert.True(files.DirectoryExists(parent));
        Assert.True(files.FileExists(path));
        Assert.Equal([0x7B, 0x7D], files.ReadAllBytes(path));
    }

    /// <summary>A second write replaces the whole file, so a shorter document leaves no tail of the longer one.</summary>
    [Fact]
    public void ASecondWriteReplacesTheWholeContent()
    {
        ISquadFileAccess files = Starting(out string root);
        string path = Path.Combine(root, "settings.json");

        files.WriteAllBytesAtomic(path, [.. "a much longer first document"u8]);
        files.WriteAllBytesAtomic(path, [.. "short"u8]);

        Assert.Equal("short", files.ReadAllText(path));
    }

    /// <summary>The bytes read back are exactly the bytes written, including ones that are not text.</summary>
    [Fact]
    public void TheBytesReadBackEqualTheBytesWritten()
    {
        ISquadFileAccess files = Starting(out string root);
        string path = Path.Combine(root, "blob.bin");
        byte[] written = [0x00, 0xFF, 0x0A, 0x0D, 0xEF, 0xBB, 0xBF, 0x80, 0x41];

        files.WriteAllBytesAtomic(path, written);

        Assert.Equal(written, files.ReadAllBytes(path));
    }

    /// <summary>The length reported after a write is the number of bytes written, read without reading the content.</summary>
    [Fact]
    public void GetLengthAfterAWriteIsTheNumberOfBytesWritten()
    {
        ISquadFileAccess files = Starting(out string root);
        string path = Path.Combine(root, "blob.bin");
        byte[] written = [0x00, 0xFF, 0x0A, 0x0D, 0xEF, 0xBB, 0xBF, 0x80, 0x41];

        files.WriteAllBytesAtomic(path, written);

        Assert.Equal(written.Length, files.GetLength(path));
    }

    /// <summary>Asking the length of a file that is not there throws, rather than reporting zero.</summary>
    [Fact]
    public void GetLengthOfAMissingFileThrowsFileNotFound()
    {
        ISquadFileAccess files = Starting(out string root);
        files.CreateDirectory(root);

        _ = Assert.Throws<FileNotFoundException>(() => files.GetLength(Path.Combine(root, "absent.json")));
    }
}
