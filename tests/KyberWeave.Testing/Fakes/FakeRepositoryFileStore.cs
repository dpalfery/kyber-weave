using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Docs.FileSystem;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The one test <see cref="IRepositoryFileStore"/>: answers through the physical store and records the reads a caller makes.
/// </summary>
/// <remarks>
/// The recorder proves a caller took its file answers from the seam it was given rather than reaching for the
/// process's file system directly. Only existence probes and full reads are recorded; that is the pair the
/// catalogue renderer consumes.
/// </remarks>
internal sealed class FakeRepositoryFileStore(IRepositoryFileStore? inner = null) : IRepositoryFileStore
{
    private readonly IRepositoryFileStore _inner = inner ?? new PhysicalRepositoryFileStore();

    /// <summary>Every path the caller asked about.</summary>
    public List<string> ExistsProbes { get; } = [];

    /// <summary>Every path the caller read whole.</summary>
    public List<string> Reads { get; } = [];

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        ExistsProbes.Add(path);
        return _inner.FileExists(path);
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    /// <inheritdoc />
    public string ReadAllText(string path)
    {
        Reads.Add(path);
        return _inner.ReadAllText(path);
    }

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

    /// <inheritdoc />
    public IEnumerable<string> ReadLines(string path) => _inner.ReadLines(path);

    /// <inheritdoc />
    public string ReadAllTextBounded(string path) => _inner.ReadAllTextBounded(path);

    /// <inheritdoc />
    public string? ReadTextIfPresent(string path) => _inner.ReadTextIfPresent(path);

    /// <inheritdoc />
    public Stream OpenRead(string path) => _inner.OpenRead(path);

    /// <inheritdoc />
    public Stream OpenAppend(string path) => _inner.OpenAppend(path);

    /// <inheritdoc />
    public string[] GetFiles(string directory) => _inner.GetFiles(directory);

    /// <inheritdoc />
    public string[] GetDirectories(string directory) => _inner.GetDirectories(directory);

    /// <inheritdoc />
    public IEnumerable<RepositoryEntry> EnumerateEntries(string directory) => _inner.EnumerateEntries(directory);

    /// <inheritdoc />
    IEnumerable<FileSystemEntry> IFileSystem.EnumerateEntries(string directory) =>
        ((IFileSystem)_inner).EnumerateEntries(directory);

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive) => _inner.DeleteDirectory(path, recursive);

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateContainedFiles(string root, string searchPattern) =>
        _inner.EnumerateContainedFiles(root, searchPattern);

    /// <inheritdoc />
    public bool IsContainedFile(string repoRoot, string relativePath, IEnumerable<string> relativeRoots) =>
        _inner.IsContainedFile(repoRoot, relativePath, relativeRoots);

    /// <inheritdoc />
    public bool IsReparsePoint(string path) => _inner.IsReparsePoint(path);

    /// <inheritdoc />
    public bool PathExists(string path) => _inner.PathExists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);

    /// <inheritdoc />
    public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents) => _inner.WriteAllTextAtomic(path, contents);

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] bytes) => _inner.WriteAllBytesAtomic(path, bytes);

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents, bool flushToDisk) =>
        _inner.WriteAllTextAtomic(path, contents, flushToDisk);

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] bytes, bool flushToDisk) =>
        _inner.WriteAllBytesAtomic(path, bytes, flushToDisk);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath) => _inner.MoveFile(sourcePath, destinationPath);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        _inner.MoveFile(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void CreateNewFile(string path) => _inner.CreateNewFile(path);

    /// <inheritdoc />
    public bool TryCreateNewFile(string path) => _inner.TryCreateNewFile(path);

    /// <inheritdoc />
    public Stream OpenWriteNew(string path) => _inner.OpenWriteNew(path);

    /// <inheritdoc />
    public void MoveDirectory(string sourcePath, string destinationPath) => _inner.MoveDirectory(sourcePath, destinationPath);

    /// <inheritdoc />
    public void DeleteFile(string path) => _inner.DeleteFile(path);

    /// <inheritdoc />
    public void DeleteTreeNoFollow(string directoryPath) => _inner.DeleteTreeNoFollow(directoryPath);

    /// <inheritdoc />
    public void CreateFileLink(string linkPath, string targetPath) => _inner.CreateFileLink(linkPath, targetPath);

    /// <inheritdoc />
    public void CreateDirectoryLink(string linkPath, string targetPath) => _inner.CreateDirectoryLink(linkPath, targetPath);

    /// <inheritdoc />
    public string CreateTemporaryDirectory(string prefix) => _inner.CreateTemporaryDirectory(prefix);

    /// <inheritdoc />
    public long GetLength(string path) => _inner.GetLength(path);

    /// <inheritdoc />
    public FileAttributes GetAttributes(string path) => _inner.GetAttributes(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        _inner.EnumerateFiles(directory, searchPattern, recursive);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks) =>
        _inner.EnumerateFiles(directory, searchPattern, recursive, followLinks);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directory) => _inner.EnumerateDirectories(directory);

    /// <inheritdoc />
    public string? GetLinkTarget(string path) => _inner.GetLinkTarget(path);

    /// <inheritdoc />
    public string? GetFinalLinkTarget(string path) => _inner.GetFinalLinkTarget(path);
}
