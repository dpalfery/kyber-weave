using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.FileSystem;

/// <summary>
/// An <see cref="IRepositoryFileStore"/> over <see cref="InMemoryFileSystem"/>: the repository checkout lives in
/// memory, a contained walk skips linked directories, and a file is contained when no link lies on its path.
/// </summary>
public sealed class InMemoryRepositoryFileStore(InMemoryFileSystem files) : IRepositoryFileStore
{
    private readonly InMemoryFileSystem _fs = files ?? throw new ArgumentNullException(nameof(files));

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        return _fs.FileExists(path);
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) => _fs.DirectoryExists(path);

    /// <inheritdoc />
    public string ReadAllText(string path)
    {
        return _fs.ReadAllText(path);
    }

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => _fs.ReadAllBytes(path);

    /// <inheritdoc />
    public IEnumerable<string> ReadLines(string path) => _fs.ReadLines(path);

    /// <inheritdoc />
    public string ReadAllTextBounded(string path) => _fs.ReadAllText(path);

    /// <inheritdoc />
    public string? ReadTextIfPresent(string path) => _fs.FileExists(path) ? _fs.ReadAllText(path) : null;

    /// <inheritdoc />
    public Stream OpenRead(string path) => _fs.OpenRead(path);

    /// <inheritdoc />
    public Stream OpenAppend(string path) => throw new NotSupportedException("The in-memory store does not append.");

    /// <inheritdoc />
    public string[] GetFiles(string directory) => [.. _fs.EnumerateFiles(directory, "*", recursive: false)];

    /// <inheritdoc />
    public string[] GetDirectories(string directory) => [.. _fs.EnumerateDirectories(directory)];

    /// <inheritdoc />
    public IEnumerable<RepositoryEntry> EnumerateEntries(string directory) =>
        _fs.EnumerateEntries(directory).Select(entry => new RepositoryEntry(entry.Path, entry.IsDirectory, entry.IsLink));

    /// <inheritdoc />
    IEnumerable<FileSystemEntry> IFileSystem.EnumerateEntries(string directory) => _fs.EnumerateEntries(directory);

    /// <inheritdoc />
    public void DeleteDirectory(string path, bool recursive) => _fs.DeleteTreeNoFollow(path);

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => _fs.GetLastWriteTimeUtc(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateContainedFiles(string root, string searchPattern)
    {
        foreach (FileSystemEntry entry in _fs.EnumerateEntries(root))
        {
            if (entry.IsLink)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                foreach (string file in EnumerateContainedFiles(entry.Path, searchPattern))
                {
                    yield return file;
                }
            }
            else if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(entry.Path)))
            {
                yield return entry.Path;
            }
        }
    }

    /// <inheritdoc />
    public bool IsContainedFile(string repoRoot, string relativePath, IEnumerable<string> relativeRoots)
    {
        string current = repoRoot;
        foreach (string segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (_fs.GetLinkTarget(current) is not null)
            {
                return false;
            }
        }

        return _fs.FileExists(current);
    }

    /// <inheritdoc />
    public bool IsReparsePoint(string path) => (_fs.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <inheritdoc />
    public bool PathExists(string path) => _fs.PathExists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path) => _fs.CreateDirectory(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string contents) => _fs.WriteAllText(path, contents);

    /// <inheritdoc />
    public void WriteAllBytes(string path, byte[] bytes) => _fs.WriteAllBytes(path, bytes);

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents) => _fs.WriteAllTextAtomic(path, contents);

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] bytes) => _fs.WriteAllBytesAtomic(path, bytes);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath) => _fs.MoveFile(sourcePath, destinationPath);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        _fs.MoveFile(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void CreateNewFile(string path) => _fs.CreateNewFile(path);

    /// <inheritdoc />
    public bool TryCreateNewFile(string path) => _fs.TryCreateNewFile(path);

    /// <inheritdoc />
    public void DeleteFile(string path) => _fs.DeleteFile(path);

    /// <inheritdoc />
    public void DeleteTreeNoFollow(string directoryPath) => _fs.DeleteTreeNoFollow(directoryPath);

    /// <inheritdoc />
    public void CreateFileLink(string linkPath, string targetPath) => _fs.CreateFileLink(linkPath, targetPath);

    /// <inheritdoc />
    public void CreateDirectoryLink(string linkPath, string targetPath) => _fs.CreateDirectoryLink(linkPath, targetPath);

    /// <inheritdoc />
    public string CreateTemporaryDirectory(string prefix) => _fs.CreateTemporaryDirectory(prefix);

    /// <inheritdoc />
    public long GetLength(string path) => _fs.GetLength(path);

    /// <inheritdoc />
    public FileAttributes GetAttributes(string path) => _fs.GetAttributes(path);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        _fs.EnumerateFiles(directory, searchPattern, recursive);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks) =>
        _fs.EnumerateFiles(directory, searchPattern, recursive, followLinks);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directory) => _fs.EnumerateDirectories(directory);

    /// <inheritdoc />
    public string? GetLinkTarget(string path) => _fs.GetLinkTarget(path);

    /// <inheritdoc />
    public string? GetFinalLinkTarget(string path) => _fs.GetFinalLinkTarget(path);
}
