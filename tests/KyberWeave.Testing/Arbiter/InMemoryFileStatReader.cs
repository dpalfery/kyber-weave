using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The file length and last write time the in-memory file system holds, for a command that stats a file.</summary>
/// <param name="files">The in-memory file system.</param>
public sealed class InMemoryFileStatReader(InMemoryFileSystem files) : IFileStatReader
{
    /// <inheritdoc />
    public long GetLength(string path) => files.GetLength(path);

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => files.GetLastWriteTimeUtc(path);
}
