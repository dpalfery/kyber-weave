using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.FileSystem;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Data.Docs.FileSystem;

/// <summary>Reads and writes the repository checkout on the real file system.</summary>
public sealed class PhysicalRepositoryFileStore : IRepositoryFileStore
{
    private readonly PhysicalFileSystem _inner = new();

    /// <inheritdoc />
    public bool FileExists(string path) => _inner.FileExists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => _inner.ReadAllText(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        _inner.EnumerateFiles(directory, searchPattern, recursive);

    /// <inheritdoc />
    public bool PathExists(string path) => _inner.PathExists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);

    /// <inheritdoc />
    public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);

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
    public void CreateDirectoryLink(string linkPath, string targetPath) =>
        _inner.CreateDirectoryLink(linkPath, targetPath);

    /// <inheritdoc />
    public string CreateTemporaryDirectory(string prefix) => _inner.CreateTemporaryDirectory(prefix);

    /// <inheritdoc />
    public long GetLength(string path) => _inner.GetLength(path);

    /// <inheritdoc />
    public FileAttributes GetAttributes(string path) => _inner.GetAttributes(path);

    /// <inheritdoc />
    public string? GetFinalLinkTarget(string path) => _inner.GetFinalLinkTarget(path);

    /// <inheritdoc />
    IEnumerable<FileSystemEntry> IFileSystem.EnumerateEntries(string directory) => _inner.EnumerateEntries(directory);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks) =>
        _inner.EnumerateFiles(directory, searchPattern, recursive, followLinks);

    /// <inheritdoc />
    public string? GetLinkTarget(string path) => _inner.GetLinkTarget(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <inheritdoc />
    public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);

    /// <inheritdoc />
    public string ReadAllTextBounded(string path) => BoundedFile.ReadAllText(path);

    /// <inheritdoc />
    public string? ReadTextIfPresent(string path) => PresentFile.ReadIfPresent(path);

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents) => AtomicFile.WriteAllText(path, contents);

    /// <inheritdoc />
    public Stream OpenRead(string path) => File.OpenRead(path);

    /// <inheritdoc />
    public Stream OpenAppend(string path) => File.Open(path, FileMode.Append, FileAccess.Write, FileShare.None);

    /// <inheritdoc />
    public string[] GetFiles(string directory) => Directory.GetFiles(directory);

    /// <inheritdoc />
    public string[] GetDirectories(string directory) => Directory.GetDirectories(directory);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directory) => Directory.EnumerateDirectories(directory);

    /// <inheritdoc />
    public IEnumerable<RepositoryEntry> EnumerateEntries(string directory)
    {
        foreach (FileSystemEntry entry in _inner.EnumerateEntries(directory))
        {
            yield return new RepositoryEntry(entry.Path, entry.IsDirectory, entry.IsLink);
        }
    }

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateContainedFiles(string root, string searchPattern) =>
        DocsRootWalk.EnumerateContainedFiles(root, searchPattern);

    /// <inheritdoc />
    public bool IsContainedFile(string repoRoot, string relativePath, IEnumerable<string> relativeRoots) =>
        DocsRootWalk.IsContainedFile(repoRoot, relativePath, relativeRoots);

    /// <inheritdoc />
    public bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
