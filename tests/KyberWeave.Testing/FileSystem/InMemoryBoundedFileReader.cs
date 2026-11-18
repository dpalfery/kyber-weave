using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.FileSystem;

/// <summary>
/// An <see cref="IBoundedFileReader"/> over <see cref="InMemoryFileSystem"/>: the bytes of a file, or null when it is
/// missing, unreadable or larger than the bound, as the disk reader answers.
/// </summary>
/// <param name="files">The in-memory file system.</param>
public sealed class InMemoryBoundedFileReader(InMemoryFileSystem files) : IBoundedFileReader
{
    /// <inheritdoc />
    public byte[]? ReadBytes(string path, int maxBytes)
    {
        try
        {
            if (!files.FileExists(path))
            {
                return null;
            }

            byte[] bytes = files.ReadAllBytes(path);
            return bytes.Length > maxBytes ? null : bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
