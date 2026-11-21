namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// The append mechanics every append-only Arbiter log shares: one complete JSON line per append under an
/// exclusive cross-process lock, and a lock-free read of a file's newest bytes.
/// </summary>
/// <remarks>
/// A log is never swept, so a read looks only at its newest bytes; a writer mid-line must never be read as a
/// record, and a log that is absent or cannot be read must read as empty rather than fail the caller. The
/// lock, the window and the UTF-8 decoding are the adapter's; which lines a log holds is its caller's.
/// </remarks>
public interface IArbiterLogStore
{
    /// <summary>Appends one line to <paramref name="fileName"/> under the exclusive lock, creating the directory when missing.</summary>
    /// <param name="directory">The directory holding the log.</param>
    /// <param name="fileName">The log's file name.</param>
    /// <param name="line">The complete line, without its newline.</param>
    /// <param name="cancellationToken">Cancels the append.</param>
    /// <exception cref="IOException">The lock was not acquired in time; the append fails closed.</exception>
    Task AppendLineAsync(string directory, string fileName, string line, CancellationToken cancellationToken);

    /// <summary>
    /// Appends the line <paramref name="lineFactory"/> produces, creating the directory when missing, under the
    /// exclusive lock. The factory is handed the lines that landed after <paramref name="snapshotLength"/> and runs
    /// under the lock; it is the caller's own lock-free read the line is normally derived from, and this call only
    /// reconciles it with what landed meanwhile.
    /// </summary>
    /// <remarks>
    /// The factory runs under the lock, so a line derived from a lock-free read cannot be derived twice from the same
    /// state: the lines that landed since the snapshot are the ones that would otherwise be missed. The factory must
    /// be pure over its input, and the caller deduplicates the snapshot lines against these by record id.
    /// </remarks>
    /// <param name="directory">The directory holding the log.</param>
    /// <param name="fileName">The log's file name.</param>
    /// <param name="snapshotLength">The log's length when the caller read it, before the lock.</param>
    /// <param name="lineFactory">Produces the complete line, without its newline, from the lines that landed since the snapshot.</param>
    /// <param name="cancellationToken">Cancels the append.</param>
    /// <exception cref="IOException">The lock was not acquired in time; the append fails closed.</exception>
    Task AppendLineAsync(
        string directory,
        string fileName,
        long snapshotLength,
        Func<IReadOnlyList<string>, string> lineFactory,
        CancellationToken cancellationToken);

    /// <summary>The log's length in bytes, taken without a lock; 0 when it is absent or cannot be read.</summary>
    /// <param name="path">The log file.</param>
    /// <returns>The length the caller's next read is taken from.</returns>
    long ByteLength(string path);

    /// <summary>Reads the complete lines of a log's newest bytes, in file order, without taking any lock.</summary>
    /// <param name="path">The log file.</param>
    /// <returns>The whole lines; empty when the file is absent or unreadable.</returns>
    IReadOnlyList<string> ReadCompleteLines(string path);

    /// <summary>
    /// <see cref="ReadCompleteLines"/> with what it left out: whether its window cut older lines off, and why a log
    /// that exists could not be read.
    /// </summary>
    /// <param name="path">The log file.</param>
    /// <returns>The lines and the diagnostics.</returns>
    LogTail ReadComplete(string path);

    /// <summary>Reads the lines inside the last <paramref name="tailBytes"/> bytes of a log, without taking any lock.</summary>
    /// <param name="path">The log file.</param>
    /// <param name="tailBytes">The window, at least 1.</param>
    /// <param name="keepUnterminatedFinalLine">Whether a final line with no newline is returned.</param>
    /// <returns>The whole lines; empty when the file is absent or unreadable.</returns>
    IReadOnlyList<string> ReadTailLines(string path, long tailBytes, bool keepUnterminatedFinalLine);

    /// <summary><see cref="ReadTailLines(string, long, bool)"/> with what it left out.</summary>
    /// <param name="path">The log file.</param>
    /// <param name="tailBytes">The window, at least 1.</param>
    /// <param name="keepUnterminatedFinalLine">Whether a final line with no newline is returned.</param>
    /// <returns>The lines and the diagnostics.</returns>
    LogTail ReadTail(string path, long tailBytes, bool keepUnterminatedFinalLine);
}

/// <summary>What one windowed read of an append-only log found, and what it could not show.</summary>
/// <param name="Lines">The whole lines in the window, in file order.</param>
/// <param name="Truncated">Whether the log was longer than the window, so older lines were not read.</param>
/// <param name="Unreadable">Why a log that exists could not be read; null when it was read or is absent.</param>
public readonly record struct LogTail(IReadOnlyList<string> Lines, bool Truncated, string? Unreadable);
