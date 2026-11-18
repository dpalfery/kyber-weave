using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// A real file system that stops a process at one chosen operation: it counts every seam call and throws
/// <see cref="SquadCrashInjectedException"/> instead of performing the operation whose ordinal was chosen, leaving the
/// disk exactly as the earlier operations left it.
/// </summary>
/// <remarks>
/// This is the real-disk counterpart of <c>InMemoryFileSystem.KillAt</c>: the same ordinals, the same "the operation does
/// not happen" meaning, so one contract suite can sweep every operation on either. An atomic write is one operation
/// here, as it is one seam call in the product; the windows inside the adapter's temporary-and-rename are the file-system
/// contract suite's to prove, not this wrapper's.
/// </remarks>
public sealed class FaultInjectingFileSystem(IWritableFileSystem inner) : IWritableFileSystem
{
    private int _killAt;

    /// <summary>Gets how many seam calls this wrapper has seen.</summary>
    public int OperationCount { get; private set; }

    /// <summary>Stops the process at one operation.</summary>
    /// <param name="ordinal">The one-based operation number.</param>
    public void KillAt(int ordinal) => _killAt = ordinal;

    /// <summary>Clears the plan, as restarting a process does; the disk survives.</summary>
    public void Restart() => _killAt = 0;

    private T Do<T>(string name, string? path, Func<T> operation)
    {
        Enter(name, path);
        return operation();
    }

    private void Do(string name, string? path, Action operation)
    {
        Enter(name, path);
        operation();
    }

    private void Enter(string name, string? path)
    {
        int number = ++OperationCount;
        if (number == _killAt)
        {
            throw new SquadCrashInjectedException($"The process was killed at operation {number} ({name} '{path}').");
        }
    }

    /// <inheritdoc />
    public bool FileExists(string path) => Do(nameof(FileExists), path, () => inner.FileExists(path));

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Do(nameof(DirectoryExists), path, () => inner.DirectoryExists(path));

    /// <inheritdoc />
    public string ReadAllText(string path) => Do(nameof(ReadAllText), path, () => inner.ReadAllText(path));

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => Do(nameof(ReadAllBytes), path, () => inner.ReadAllBytes(path));

    /// <inheritdoc />
    public IEnumerable<string> ReadLines(string path) => Do(nameof(ReadLines), path, () => inner.ReadLines(path));

    /// <inheritdoc />
    public Stream OpenRead(string path) => Do(nameof(OpenRead), path, () => inner.OpenRead(path));

    /// <inheritdoc />
    public long GetLength(string path) => Do(nameof(GetLength), path, () => inner.GetLength(path));

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) =>
        Do(nameof(GetLastWriteTimeUtc), path, () => inner.GetLastWriteTimeUtc(path));

    /// <inheritdoc />
    public FileAttributes GetAttributes(string path) => Do(nameof(GetAttributes), path, () => inner.GetAttributes(path));

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        Do(nameof(EnumerateFiles), directory, () => inner.EnumerateFiles(directory, searchPattern, recursive));

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks) =>
        Do(nameof(EnumerateFiles), directory, () => inner.EnumerateFiles(directory, searchPattern, recursive, followLinks));

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directory) =>
        Do(nameof(EnumerateDirectories), directory, () => inner.EnumerateDirectories(directory));

    /// <inheritdoc />
    public IEnumerable<FileSystemEntry> EnumerateEntries(string directory) =>
        Do(nameof(EnumerateEntries), directory, () => inner.EnumerateEntries(directory));

    /// <inheritdoc />
    public string? GetLinkTarget(string path) => Do(nameof(GetLinkTarget), path, () => inner.GetLinkTarget(path));

    /// <inheritdoc />
    public string? GetFinalLinkTarget(string path) =>
        Do(nameof(GetFinalLinkTarget), path, () => inner.GetFinalLinkTarget(path));

    /// <inheritdoc />
    public bool PathExists(string path) => Do(nameof(PathExists), path, () => inner.PathExists(path));

    /// <inheritdoc />
    public void CreateDirectory(string path) => Do(nameof(CreateDirectory), path, () => inner.CreateDirectory(path));

    /// <inheritdoc />
    public void WriteAllText(string path, string contents) =>
        Do(nameof(WriteAllText), path, () => inner.WriteAllText(path, contents));

    /// <inheritdoc />
    public void WriteAllBytes(string path, byte[] bytes) =>
        Do(nameof(WriteAllBytes), path, () => inner.WriteAllBytes(path, bytes));

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents) =>
        Do(nameof(WriteAllTextAtomic), path, () => inner.WriteAllTextAtomic(path, contents));

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents, bool flushToDisk) =>
        Do(nameof(WriteAllTextAtomic), path, () => inner.WriteAllTextAtomic(path, contents, flushToDisk));

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] bytes) =>
        Do(nameof(WriteAllBytesAtomic), path, () => inner.WriteAllBytesAtomic(path, bytes));

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] bytes, bool flushToDisk) =>
        Do(nameof(WriteAllBytesAtomic), path, () => inner.WriteAllBytesAtomic(path, bytes, flushToDisk));

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath) =>
        Do(nameof(MoveFile), sourcePath, () => inner.MoveFile(sourcePath, destinationPath));

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        Do(nameof(MoveFile), sourcePath, () => inner.MoveFile(sourcePath, destinationPath, overwrite));

    /// <inheritdoc />
    public void CreateNewFile(string path) => Do(nameof(CreateNewFile), path, () => inner.CreateNewFile(path));

    /// <inheritdoc />
    public bool TryCreateNewFile(string path) => Do(nameof(TryCreateNewFile), path, () => inner.TryCreateNewFile(path));

    /// <inheritdoc />
    public Stream OpenWriteNew(string path) => Do(nameof(OpenWriteNew), path, () => inner.OpenWriteNew(path));

    /// <inheritdoc />
    public void MoveDirectory(string sourcePath, string destinationPath) =>
        Do(nameof(MoveDirectory), sourcePath, () => inner.MoveDirectory(sourcePath, destinationPath));

    /// <inheritdoc />
    public void DeleteFile(string path) => Do(nameof(DeleteFile), path, () => inner.DeleteFile(path));

    /// <inheritdoc />
    public void DeleteTreeNoFollow(string directoryPath) =>
        Do(nameof(DeleteTreeNoFollow), directoryPath, () => inner.DeleteTreeNoFollow(directoryPath));

    /// <inheritdoc />
    public void CreateFileLink(string linkPath, string targetPath) =>
        Do(nameof(CreateFileLink), linkPath, () => inner.CreateFileLink(linkPath, targetPath));

    /// <inheritdoc />
    public void CreateDirectoryLink(string linkPath, string targetPath) =>
        Do(nameof(CreateDirectoryLink), linkPath, () => inner.CreateDirectoryLink(linkPath, targetPath));

    /// <inheritdoc />
    public string CreateTemporaryDirectory(string prefix) =>
        Do(nameof(CreateTemporaryDirectory), prefix, () => inner.CreateTemporaryDirectory(prefix));
}
