using System.Diagnostics;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Data.FileSystem;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The append mechanics both files share: one complete JSON line per append,
/// written under an exclusive lock on <c>artifacts/arbiter/.lock</c>.
/// </summary>
/// <remarks>
/// The lock is taken with <see cref="FileShare.None"/>, which .NET enforces at
/// the OS level between processes (and between handles), so mutual exclusion
/// holds across hook processes, not just threads. Only the write itself is under
/// the lock: a line derived from the log is computed from a lock-free read, and
/// the append reconciles what landed meanwhile instead of re-reading the log under
/// the lock. Holding the lock across a whole-log read made twenty concurrent hooks
/// miss the wait budget and lose their rows. A lock that cannot be taken within
/// three 500 ms attempts fails closed (<c>KW-ARB-HOOK-001</c>) rather than writing an
/// unserialized line, so a burst retries briefly instead of losing its first row.
/// Readers, by contrast, take no lock and tolerate a torn final line, because a writer
/// may be mid-line when they read.
/// </remarks>
public sealed class ArbiterLogFiles : IArbiterLogStore
{
    private const string LockFileName = ".lock";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// How far before the caller's snapshot the reconcile read reaches: enough to include the whole line a snapshot
    /// taken mid-write would have cut, so the line is parsed whole rather than dropped as a fragment.
    /// </summary>
    private const long ReconcileLookbackBytes = 64 * 1024;

    /// <summary>
    /// The most bytes one read of an append-only Arbiter log looks at, taken from the end. A log is never swept, so
    /// reading all of it would make every hook call cost more as the log ages; the newest records are the ones the
    /// in-flight and decision queries need.
    /// </summary>
    public const long MaxReadBytes = 8L * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;

    /// <summary>How many times one append tries to take the lock before it fails closed.</summary>
    private const int LockAttempts = 3;

    /// <summary>How long one attempt waits for the lock.</summary>
    private static readonly TimeSpan LockWaitBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the append pauses between two attempts.</summary>
    private static readonly TimeSpan LockRetryPause = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public long ByteLength(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return File.Exists(path) ? new FileInfo(SymbolicLinks.FinalTarget(path)).Length : 0;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task AppendLineAsync(
        string directory,
        string fileName,
        string line,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using FileStream lockFile = await AcquireLockAsync(
            Path.Combine(directory, LockFileName), cancellationToken).ConfigureAwait(false);
        await WriteLineAsync(path, line, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AppendLineAsync(
        string directory,
        string fileName,
        long snapshotLength,
        Func<IReadOnlyList<string>, string> lineFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineFactory);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotLength);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using FileStream lockFile = await AcquireLockAsync(
            Path.Combine(directory, LockFileName), cancellationToken).ConfigureAwait(false);

        // The reconcile read is bounded by what landed since the snapshot, so the critical section stays the
        // append alone. Another appender can only land before this lock is taken, and must release it first.
        IReadOnlyList<string> landed = ReadSince(path, snapshotLength);
        await WriteLineAsync(path, lineFactory(landed), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one line as one append. One write per line under an O_APPEND descriptor: the kernel places it at the
    /// end, so parallel processes append whole lines and never write over each other's.
    /// </summary>
    private static async Task WriteLineAsync(string path, string line, CancellationToken cancellationToken)
    {
        using Stream target = AppendFile.Open(path);
        byte[] bytes = Utf8NoBom.GetBytes(line + "\n");
        await target.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the exclusive append lock: three attempts, each waiting with backoff inside a 500 ms budget.</summary>
    /// <remarks>
    /// A burst of concurrent hooks kept losing rows when the wait budget ran out on the first attempt and the append
    /// failed closed. The lock is held only for one line's write, so a second and third attempt a short pause later
    /// almost always find it free, without ever waiting an unbounded time.
    /// </remarks>
    private static async Task<FileStream> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        IOException? failure = null;
        for (int attempt = 1; attempt <= LockAttempts; attempt++)
        {
            (FileStream? stream, IOException? attemptFailure) =
                await TryAcquireAsync(lockPath, cancellationToken).ConfigureAwait(false);
            if (stream is not null)
            {
                return stream;
            }

            failure = attemptFailure;
            if (attempt < LockAttempts)
            {
                await Task.Delay(LockRetryPause, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            $"Could not acquire the exclusive Arbiter append lock '{lockPath}' within {LockAttempts} attempts of " +
            $"{LockWaitBudget.TotalMilliseconds:0} ms: the append fails closed (KW-ARB-HOOK-001).",
            failure);
    }

    /// <summary>One attempt inside the wait budget: the lock, or the last failure that kept it from being taken.</summary>
    private static async Task<(FileStream? Stream, IOException? Failure)> TryAcquireAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan backoff = TimeSpan.FromMilliseconds(2);

        while (true)
        {
            try
            {
                return (File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), null);
            }
            catch (IOException) when (elapsed.Elapsed < LockWaitBudget)
            {
                // A waiter that is asleep cannot take the lock the moment it frees, and a burst of twenty hooks
                // spends the whole wait budget trading 50 ms sleeps while the lock sits idle between them. The
                // backoff is short and capped low instead, so the lock is taken as soon as it is released.
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                backoff = TimeSpan.FromMilliseconds(Math.Min(5, backoff.TotalMilliseconds * 2));
            }
            catch (IOException ex)
            {
                return (null, ex);
            }
        }
    }

    /// <summary>
    /// The complete lines that landed after <paramref name="snapshotLength"/>, read from a small lookback so a line
    /// the snapshot cut in half is still parsed whole. A caller that read the log before taking the snapshot sees
    /// its rows twice; the caller deduplicates by record id.
    /// </summary>
    private IReadOnlyList<string> ReadSince(string path, long snapshotLength)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (length <= snapshotLength)
            {
                return [];
            }

            long window = Math.Min(length, length - snapshotLength + ReconcileLookbackBytes);
            return ReadTail(stream, window, keepUnterminatedFinalLine: true).Lines;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return [];
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
