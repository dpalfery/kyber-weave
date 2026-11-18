using System.Text;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The append-only log seam in memory: each append is one line, reads take a window of the newest bytes, and a test
/// can make an append fail or a file unreadable without touching a disk.
/// </summary>
public sealed class FakeArbiterLogStore : IArbiterLogStore
{
    private readonly Dictionary<string, List<string>> _lines = new(StringComparer.Ordinal);

    /// <summary>The files whose reads answer "unreadable", as <see cref="LogTail.Unreadable"/> records.</summary>
    public HashSet<string> UnreadablePaths { get; } = new(StringComparer.Ordinal);

    /// <summary>What an append throws, when a test makes the log unavailable.</summary>
    public Exception? AppendFailure { get; set; }

    /// <summary>How many appends this log has taken.</summary>
    public int AppendCount { get; private set; }

    /// <summary>The lines appended to one file, oldest first.</summary>
    /// <param name="directory">The directory the append named.</param>
    /// <param name="fileName">The file the append named.</param>
    public IReadOnlyList<string> LinesOf(string directory, string fileName) =>
        _lines.TryGetValue(Path.Combine(directory, fileName), out List<string>? lines) ? lines : [];

    /// <inheritdoc />
    public Task AppendLineAsync(string directory, string fileName, string line, CancellationToken cancellationToken) =>
        AppendLineAsync(directory, fileName, (Func<string>)(() => line), cancellationToken);

    /// <inheritdoc />
    public Task AppendLineAsync(string directory, string fileName, Func<string> lineFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lineFactory);
        if (AppendFailure is { } failure)
        {
            throw failure;
        }

        string path = Path.Combine(directory, fileName);
        if (!_lines.TryGetValue(path, out List<string>? lines))
        {
            lines = [];
            _lines[path] = lines;
        }

        lines.Add(lineFactory());
        AppendCount++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadCompleteLines(string path) => Window(path, long.MaxValue).Lines;

    /// <inheritdoc />
    public LogTail ReadComplete(string path) => Window(path, long.MaxValue);

    /// <inheritdoc />
    public IReadOnlyList<string> ReadTailLines(string path, long tailBytes, bool keepUnterminatedFinalLine) =>
        Window(path, tailBytes).Lines;

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

        if (!_lines.TryGetValue(path, out List<string>? lines) || lines.Count == 0)
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
            if (!truncated)
            {
                kept.Insert(0, lines[index]);
                continue;
            }

            if (lineBytes > remaining)
            {
                break;
            }

            kept.Insert(0, lines[index]);
            remaining -= lineBytes;
        }

        return new LogTail(kept, truncated, Unreadable: null);
    }
}
