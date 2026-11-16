using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Closeout;
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
    public void CreateNewFile(string path) => _inner.CreateNewFile(path);

    /// <inheritdoc />
    public void DeleteFile(string path) => _inner.DeleteFile(path);

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
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos(
                     "*",
                     new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 }))
        {
            yield return new RepositoryEntry(entry.FullName, (entry.Attributes & FileAttributes.Directory) != 0, entry.LinkTarget is not null);
        }
    }

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateContainedFiles(string root, string searchPattern) =>
        DocsRootPath.EnumerateContainedFiles(root, searchPattern);

    /// <inheritdoc />
    public bool IsContainedFile(string repoRoot, string relativePath, IEnumerable<string> relativeRoots) =>
        DocsRootPath.IsContainedFile(repoRoot, relativePath, relativeRoots);

    /// <inheritdoc />
    public bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
