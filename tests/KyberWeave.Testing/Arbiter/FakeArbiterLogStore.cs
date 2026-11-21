using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The append-only log seam in memory: each append is one line, reads take a window of the newest bytes, and a test
/// can make an append fail or a file unreadable without touching a disk.
/// </summary>
/// <remarks>
/// The fake keeps the adapter's answers for the cases a caller can observe: a window under one byte is a programming
/// error and throws, the complete read is the adapter's 8 MiB window, and an append that was cancelled stores nothing.
/// A fake that answered any of those differently would let a unit test pass where production fails.
/// </remarks>
public sealed class FakeArbiterLogStore : IArbiterLogStore
{
    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>The files whose reads answer "unreadable", as <see cref="LogTail.Unreadable"/> records.</summary>
    public HashSet<string> UnreadablePaths { get; } = new(StringComparer.Ordinal);

    /// <summary>What an append throws, when a test makes the log unavailable.</summary>
    public Exception? AppendFailure { get; set; }

    /// <summary>The paths an append has named, in first-append order. A test reads this to see which files exist.</summary>
    /// <remarks>
    /// The fake holds lines in a dictionary and never touches the file system, so a test asking "was anything stored
    /// here" must ask this log rather than the file system: a file-system check would answer false for every input.
    /// </remarks>
    public IReadOnlyList<string> AppendedPaths
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines.Keys];
            }
        }
    }

    /// <summary>How many appends this log has taken. A cancelled or refused append is not counted.</summary>
    public int AppendCount
    {
        get
        {
            lock (_gate)
            {
                return _appendCount;
            }
        }
    }

    private int _appendCount;

    /// <summary>The lines appended to one file, oldest first.</summary>
    /// <param name="directory">The directory the append named.</param>
    /// <param name="fileName">The file the append named.</param>
    public IReadOnlyList<string> LinesOf(string directory, string fileName)
    {
        lock (_gate)
        {
            return _lines.TryGetValue(Path.Combine(directory, fileName), out List<string>? lines) ? [.. lines] : [];
        }
    }

    /// <inheritdoc />
    public long ByteLength(string path)
    {
        lock (_gate)
        {
            return _lines.TryGetValue(path, out List<string>? lines)
                ? lines.Sum(line => Encoding.UTF8.GetByteCount(line) + 1)
                : 0;
        }
    }

    /// <inheritdoc />
    public Task AppendLineAsync(string directory, string fileName, string line, CancellationToken cancellationToken) =>
        AppendLineAsync(directory, fileName, snapshotLength: 0, _ => line, cancellationToken);

    /// <inheritdoc />
    public Task AppendLineAsync(
        string directory,
        string fileName,
        long snapshotLength,
        Func<IReadOnlyList<string>, string> lineFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineFactory);
        cancellationToken.ThrowIfCancellationRequested();
        if (AppendFailure is { } failure)
        {
            throw failure;
        }

        string path = Path.Combine(directory, fileName);
        string line;
        lock (_gate)
        {
            // The adapter's contract: the factory sees the lines that ended after the snapshot, and the write is one
            // more line after whatever is already stored.
            List<string> lines = _lines.TryGetValue(path, out List<string>? existing) ? existing : [];
            List<string> landed = [];
            long offset = 0;
            foreach (string stored in lines)
            {
                offset += Encoding.UTF8.GetByteCount(stored) + 1;
                if (offset > snapshotLength)
                {
                    landed.Add(stored);
                }
            }

            line = lineFactory(landed);
            if (!_lines.ContainsKey(path))
            {
                _lines[path] = lines;
            }

            lines.Add(line);
            _appendCount++;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadCompleteLines(string path) => ReadComplete(path).Lines;

    /// <inheritdoc />
    public LogTail ReadComplete(string path) => Window(path, ArbiterLogFiles.MaxReadBytes);

    /// <inheritdoc />
    public IReadOnlyList<string> ReadTailLines(string path, long tailBytes, bool keepUnterminatedFinalLine) =>
        ReadTail(path, tailBytes, keepUnterminatedFinalLine).Lines;

    /// <inheritdoc />
    public LogTail ReadTail(string path, long tailBytes, bool keepUnterminatedFinalLine)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tailBytes, 1);
        return Window(path, tailBytes);
    }

    /// <summary>The newest whole lines that fit <paramref name="tailBytes"/>, as the real reader takes them.</summary>
    private LogTail Window(string path, long tailBytes)
    {
        if (UnreadablePaths.Contains(path))
        {
            return new LogTail([], Truncated: false, Unreadable: "IOException: the fake log is unreadable");
        }

        List<string> lines;
        lock (_gate)
        {
            lines = _lines.TryGetValue(path, out List<string>? stored) ? [.. stored] : [];
        }

        if (lines.Count == 0)
        {
            return new LogTail([], Truncated: false, Unreadable: null);
        }

        long bytes = lines.Sum(line => Encoding.UTF8.GetByteCount(line) + 1);
        bool truncated = bytes > tailBytes;
        List<string> kept = [];
        long remaining = tailBytes;
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            long lineBytes = Encoding.UTF8.GetByteCount(lines[index]) + 1;
            if (truncated && lineBytes > remaining)
            {
                break;
            }

            kept.Insert(0, lines[index]);
            remaining -= lineBytes;
        }

        return new LogTail(kept, truncated, Unreadable: null);
    }
}
