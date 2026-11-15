using System.Text;

namespace KyberWeave.Core.FileSystem;

/// <summary>
/// The one way this product reads a file whose size it does not control. The bound counts the bytes actually read from
/// the open stream, not a length looked up beforehand, so a file that grows between a size check and the read, or one
/// that reports a smaller size than it holds, is still refused.
/// </summary>
internal static class BoundedFileReader
{
    private const int ChunkBytes = 81920;

    /// <summary>The largest bound accepted: the text is held in one in-memory buffer, which cannot pass this size.</summary>
    internal const long MaxLimitBytes = int.MaxValue;

    /// <summary>
    /// The file's text, with a byte-order mark detected as <see cref="File.ReadAllText(string)"/> detects it. At most
    /// <paramref name="limitBytes"/> + 1 bytes are ever read.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <param name="limitBytes">The most bytes the file may hold, from 0 to <see cref="MaxLimitBytes"/>.</param>
    /// <exception cref="InvalidDataException">The file holds more than <paramref name="limitBytes"/> bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limitBytes"/> is negative or above <see cref="MaxLimitBytes"/>.</exception>
    internal static string ReadAllText(string path, long limitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limitBytes, MaxLimitBytes);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadAllText(stream, limitBytes);
    }

    /// <summary>The stream's remaining text, bounded as <see cref="ReadAllText(string, long)"/> bounds a file.</summary>
    /// <param name="stream">The stream to read to its end.</param>
    /// <param name="limitBytes">The most bytes the stream may yield.</param>
    /// <exception cref="InvalidDataException">The stream yields more than <paramref name="limitBytes"/> bytes.</exception>
    internal static string ReadAllText(Stream stream, long limitBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(limitBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limitBytes, MaxLimitBytes);
        using MemoryStream bytes = new();
        byte[] chunk = new byte[ChunkBytes];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, limitBytes + 1 - total))) > 0)
        {
            total += read;
            if (total > limitBytes)
            {
                throw new InvalidDataException($"the file is at least {total} bytes, more than the {limitBytes} bytes this step reads");
            }

            bytes.Write(chunk, 0, read);
        }

        bytes.Position = 0;
        using StreamReader reader = new(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The file's lines, split as <see cref="File.ReadAllLines(string)"/> splits them, under the same bound.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="limitBytes">The most bytes the file may hold.</param>
    /// <exception cref="InvalidDataException">The file holds more than <paramref name="limitBytes"/> bytes.</exception>
    internal static string[] ReadAllLines(string path, long limitBytes)
    {
        using StringReader reader = new(ReadAllText(path, limitBytes));
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }
}
