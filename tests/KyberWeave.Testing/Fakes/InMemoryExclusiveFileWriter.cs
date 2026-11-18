using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The exclusive create over the in-memory file system: a path that is already taken (a file or a folder) is refused and
/// left alone, a new path is created and written. The disk's own failure modes (a denied folder, a write that fails
/// after the create) are the real adapter's, proved in the integration project.
/// </summary>
internal sealed class InMemoryExclusiveFileWriter(InMemoryFileSystem files) : IExclusiveFileWriter
{
    /// <inheritdoc />
    public bool TryWriteNew(string path, string text) => TryWriteNew(path, text, files.DeleteFile);

    /// <inheritdoc />
    public bool TryWriteNew(string path, string text, Action<string> deletePartial)
    {
        ArgumentNullException.ThrowIfNull(deletePartial);
        if (files.PathExists(path) || !files.TryCreateNewFile(path))
        {
            return false;
        }

        try
        {
            files.WriteAllText(path, text);
            return true;
        }
        catch
        {
            try
            {
                deletePartial(path);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The write error is the one to report.
            }

            throw;
        }
    }
}
