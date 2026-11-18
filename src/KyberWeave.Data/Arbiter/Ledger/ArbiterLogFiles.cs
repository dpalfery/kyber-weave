using System.Diagnostics;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The append mechanics both files share: one complete JSON line per append,
/// written under an exclusive lock on <c>artifacts/arbiter/.lock</c>.
/// </summary>
/// <remarks>
/// The lock is taken with <see cref="FileShare.None"/>, which .NET enforces at
/// the OS level between processes (and between handles), so mutual exclusion
/// holds across hook processes, not just threads. A lock that cannot be taken
/// within the 500 ms budget is an internal error: the caller fails closed
/// (<c>KW-ARB-HOOK-001</c>) rather than writing an unserialized line. Readers,
/// by contrast, take no lock and tolerate a torn final line, because a writer
/// may be mid-line when they read.
/// </remarks>
public sealed class ArbiterLogFiles : IArbiterLogStore
{
    private const string LockFileName = ".lock";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The most bytes one read of an append-only Arbiter log looks at, taken from the end. A log is never swept, so
    /// reading all of it would make every hook call cost more as the log ages; the newest records are the ones the
    /// in-flight and decision queries need.
    /// </summary>
    public const long MaxReadBytes = 8L * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;

    /// <inheritdoc />
    public Task AppendLineAsync(
        string directory,
        string fileName,
        string line,
        CancellationToken cancellationToken) =>
        AppendLineAsync(directory, fileName, (Func<string>)(() => line), cancellationToken);

    /// <inheritdoc />
    public async Task AppendLineAsync(
        string directory,
        string fileName,
        Func<string> lineFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineFactory);
        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(directory, LockFileName);
        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan backoff = TimeSpan.FromMilliseconds(5);

        while (true)
        {
            FileStream lockFile;
            try
            {
                lockFile = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromMilliseconds(500))
            {
                // Exponential backoff; the retry window stays inside the hook
                // budget, and a lock that is still held when it expires throws
                // rather than writes.
                await Task.Delay(backoff, cancellationToken);
                backoff = TimeSpan.FromMilliseconds(Math.Min(50, backoff.TotalMilliseconds * 2));
                continue;
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Could not acquire the exclusive Arbiter append lock '{lockPath}' within 500 ms: the append fails closed (KW-ARB-HOOK-001).",
                    ex);
            }

            try
            {
                // Resolved under the lock, not before it: a post's pairing decision depends
                // on which pre events are still unpaired, so deciding it outside the lock
                // lets racing posts all claim the same pre and double-book a dispatch.
                string line = lineFactory();
                await using FileStream target = File.Open(
                    Path.Combine(directory, fileName), FileMode.Append, FileAccess.Write, FileShare.Read);
                await using StreamWriter writer = new(target, Utf8NoBom);
                await writer.WriteAsync(line.AsMemory(), cancellationToken);
                await writer.WriteAsync("\n".AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
            finally
            {
                await lockFile.DisposeAsync();
            }

            return;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadCompleteLines(string path) => ReadComplete(path).Lines;

    /// <inheritdoc />
    public LogTail ReadComplete(string path) =>
        ReadTail(path, MaxReadBytes, keepUnterminatedFinalLine: false);

    /// <inheritdoc />
    public IReadOnlyList<string> ReadTailLines(string path, long tailBytes, bool keepUnterminatedFinalLine) =>
        ReadTail(path, tailBytes, keepUnterminatedFinalLine).Lines;

    /// <inheritdoc />
    public LogTail ReadTail(string path, long tailBytes, bool keepUnterminatedFinalLine)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tailBytes, 1);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadTail(stream, tailBytes, keepUnterminatedFinalLine);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new LogTail([], Truncated: false, Unreadable: null);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return new LogTail([], Truncated: false, Unreadable: $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// The stream form of <see cref="ReadTailLines(string, long, bool)"/>. It catches nothing: an
    /// <see cref="IOException"/> from the stream propagates, and only the path overloads turn it into an empty read.
    /// </summary>
    /// <param name="stream">The stream to read.</param>
    /// <param name="tailBytes">The window, at least 1.</param>
    /// <param name="keepUnterminatedFinalLine">Whether a final line with no newline is returned.</param>
    /// <returns>The whole lines in the window, in file order.</returns>
    public IReadOnlyList<string> ReadTailLines(Stream stream, long tailBytes, bool keepUnterminatedFinalLine) =>
        ReadTail(stream, tailBytes, keepUnterminatedFinalLine).Lines;

    /// <summary>The stream form of <see cref="ReadTail(string, long, bool)"/>; it catches nothing.</summary>
    /// <remarks>
    /// The window is read in fixed chunks and cut into lines as it goes, so the memory held is one chunk, the line
    /// being assembled and the lines kept, not a buffer, a string and a split of the whole window.
    /// </remarks>
    /// <param name="stream">The stream to read.</param>
    /// <param name="tailBytes">The window, at least 1.</param>
    /// <param name="keepUnterminatedFinalLine">Whether a final line with no newline is returned.</param>
    /// <returns>The lines and the diagnostics.</returns>
    public LogTail ReadTail(Stream stream, long tailBytes, bool keepUnterminatedFinalLine)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(tailBytes, 1);

        // Length is read once: a second read of a file that is being appended to, or truncated, could disagree with
        // the first and yield a negative seek or a window of the wrong size.
        long length = stream.Length;
        long window = Math.Min(length, tailBytes);
        bool truncated = length > tailBytes;
        bool startsMidLine = false;
        if (truncated)
        {
            stream.Seek(length - tailBytes - 1, SeekOrigin.Begin);
            startsMidLine = stream.ReadByte() != '\n';
        }

        List<string> lines = [];
        byte[] chunk = new byte[Math.Min(window, ChunkBytes)];
        using MemoryStream pending = new();
        long remaining = window;
        int segment = 0;
        while (remaining > 0)
        {
            int read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
            if (read == 0)
            {
                break;
            }

            remaining -= read;
            int start = 0;
            while (start < read)
            {
                int newline = Array.IndexOf(chunk, (byte)'\n', start, read - start);
                if (newline < 0)
                {
                    pending.Write(chunk, start, read - start);
                    break;
                }

                pending.Write(chunk, start, newline - start);
                AddSegment(lines, pending, segment++, startsMidLine);
                start = newline + 1;
            }
        }

        // What is left has no newline after it: a writer mid-line or a torn write. The first segment is a fragment
        // when the window opened mid-line (and possibly mid-character), and is never kept.
        if (pending.Length > 0 && keepUnterminatedFinalLine)
        {
            AddSegment(lines, pending, segment, startsMidLine);
        }

        return new LogTail(lines, truncated, Unreadable: null);
    }

    private static void AddSegment(List<string> lines, MemoryStream segmentBytes, int index, bool startsMidLine)
    {
        if (index > 0 || !startsMidLine)
        {
            lines.Add(Encoding.UTF8.GetString(segmentBytes.GetBuffer(), 0, (int)segmentBytes.Length).TrimEnd('\r'));
        }

        segmentBytes.SetLength(0);
    }
}
