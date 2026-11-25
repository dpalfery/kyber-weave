using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.FileSystem;

namespace KyberWeave.Data.Arbiter;

/// <summary>Reads a file's length and last write time from the file system, following symbolic links.</summary>
public sealed class FileStatReader : IFileStatReader
{
    /// <inheritdoc />
    public long GetLength(string path) => new FileInfo(SymbolicLinks.FinalTarget(path)).Length;

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(SymbolicLinks.FinalTarget(path));
}
