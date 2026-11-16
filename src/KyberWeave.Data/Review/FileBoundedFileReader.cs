using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Reads files from the file system, counting the bytes actually read so a file that grows mid-read is still refused.</summary>
public sealed class FileBoundedFileReader : IBoundedFileReader
{
    /// <inheritdoc />
    public byte[]? ReadBytes(string path, int maxBytes)
    {
        byte[] buffer = new byte[maxBytes + 1];
        int totalRead = 0;
        try
        {
            if (!File.Exists(path))
                return null;

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maxBytes)
                return null;

            while (totalRead <= maxBytes)
            {
                int read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }

            if (totalRead > maxBytes)
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }

        return buffer[..totalRead];
    }
}
