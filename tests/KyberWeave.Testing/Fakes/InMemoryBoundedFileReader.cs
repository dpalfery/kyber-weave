using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Fakes;

/// <summary>Reads a bounded file from the in-memory file system, refusing one that is absent, past the limit, or a name
/// the platform cannot take.</summary>
internal sealed class InMemoryBoundedFileReader(InMemoryFileSystem files) : IBoundedFileReader
{
    /// <inheritdoc/>
    public byte[]? ReadBytes(string path, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Null character in path.", nameof(path));
        }

        return files.FileExists(path) && files.ReadAllBytes(path) is { } bytes && bytes.Length <= maxBytes
            ? bytes
            : null;
    }
}
