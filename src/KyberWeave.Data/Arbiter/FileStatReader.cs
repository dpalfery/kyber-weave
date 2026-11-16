using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Arbiter;

/// <summary>Reads a file's length and last write time from the file system.</summary>
public sealed class FileStatReader : IFileStatReader
{
    /// <inheritdoc />
    public long GetLength(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
}
