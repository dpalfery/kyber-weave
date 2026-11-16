using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Docs.Closeout;

/// <summary>The exclusive-create file writer over the physical file system.</summary>
public sealed class ExclusiveFileWriter : IExclusiveFileWriter
{
    /// <inheritdoc />
    public bool TryWriteNew(string path, string text) => TryWriteNew(path, text, File.Delete);

    /// <inheritdoc />
    public bool TryWriteNew(string path, string text, Action<string> deletePartial)
    {
        ArgumentNullException.ThrowIfNull(deletePartial);
        bool created = false;
        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            using StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            writer.Write(text);
            return true;
        }
        catch (UnauthorizedAccessException) when (!created)
        {
            // A folder in the way, or no permission to create here: nothing was written.
            return false;
        }
        catch (IOException exception) when (!created && IsPathTaken(exception))
        {
            return false;
        }
        catch when (created)
        {
            // Best effort: the write failure is the error to report, and a removal that fails too must not replace it.
            // A partial file that cannot be removed stays on disk; the caller sees the write error, not a clean refusal.
            try
            {
                deletePartial(path);
            }
            catch (Exception cleanup) when (FileFailure.Is(cleanup))
            {
                // The original exception is rethrown below.
            }

            throw;
        }
    }

    /// <summary>Whether a failed exclusive create failed because the path already exists (EEXIST; ERROR_FILE_EXISTS or ERROR_ALREADY_EXISTS).</summary>
    private static bool IsPathTaken(IOException exception)
    {
        const int Eexist = 17;
        const int ErrorFileExists = 80;
        const int ErrorAlreadyExists = 183;
        return OperatingSystem.IsWindows()
            ? (exception.HResult & 0xFFFF) is ErrorFileExists or ErrorAlreadyExists
            : exception.HResult == Eexist;
    }
}
