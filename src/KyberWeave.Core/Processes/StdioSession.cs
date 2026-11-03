using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace KyberWeave.Core.Processes;

/// <summary>
///     A bounded, two-way, line-delimited conversation with one long-lived child process over its standard streams.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ProcessRunner.Run"/> writes all of its input, closes it, and then waits, which cannot carry a
///         request-response exchange such as a JSON-RPC handshake. This session is the second spawn path, and it
///         keeps the same safety rules: the start information is built by <see cref="ProcessRunner"/>, and the
///         process tree is killed in <see cref="Dispose"/>.
///     </para>
///     <para>
///         Dispose kills the tree on every path, not only when the budget ran out. A stuck server returns no line,
///         and a dispose that only closed stdin would leave it running. Callers therefore dispose in a
///         <c>using</c> block whatever the outcome, and a run that leaves a child behind is a failed run.
///     </para>
///     <para>
///         One total budget covers the whole session. Each read waits only for what remains of it, so a server
///         that answers slowly cannot extend the run past the budget.
///     </para>
/// </remarks>
public sealed class StdioSession : IDisposable
{
    /// <summary>The longest Dispose waits for the killed tree to be reaped before it gives up.</summary>
    private static readonly TimeSpan ReapWait = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     The most characters one line may hold before the session refuses to read it.
    /// </summary>
    /// <remarks>
    ///     The budget bounds time, not size. A reader that accumulates until it sees a terminator grows without
    ///     limit when the child never writes one, so a stuck or hostile server could exhaust memory long before the
    ///     budget expired. One mebibyte is far above any JSON-RPC reply a first-party server sends.
    /// </remarks>
    private const int MaxLineLength = 1024 * 1024;

    /// <summary>The size of one read from the child's standard output.</summary>
    private const int ReadBufferLength = 4096;

    /// <summary>
    ///     The longest wait <c>Task.WaitAsync</c> accepts. The budget is a caller's value with no upper bound of its
    ///     own, and passing one past this limit throws instead of waiting.
    /// </summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Process _process;
    private readonly int _processId;
    private readonly DateTime _deadline;
    private readonly Task<string?> _firstErrorLine;

    /// <summary>Characters read from the child but not yet returned, when one read crossed a line end.</summary>
    private string _unread = string.Empty;

    private Task<string?>? _pendingRead;
    private bool _disposed;

    /// <summary>Starts <paramref name="fileName"/> with the given arguments, bounded by <paramref name="budget"/>.</summary>
    /// <param name="fileName">The executable. A bare name is resolved through PATH; no shell is involved.</param>
    /// <param name="arguments">The argument vector, passed through <see cref="ProcessStartInfo.ArgumentList"/>.</param>
    /// <param name="budget">The total time the session may take across every read it makes. Must be positive.</param>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    public StdioSession(string fileName, IReadOnlyList<string> arguments, TimeSpan budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);

        ProcessStartInfo startInfo = new(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        _deadline = DateTime.UtcNow + budget;
        _process = Process.Start(ProcessRunner.CreateSafeStartInfo(startInfo))
                   ?? throw new InvalidOperationException("The child process could not be started.");
        _processId = _process.Id;

        // Stderr is drained for the life of the child, not left to fill its pipe and block it. Only the first
        // non-empty line is kept: it is the one a failure message may show, and the rest is not disclosed.
        _firstErrorLine = Task.Run(DrainFirstErrorLineAsync);
    }

    /// <summary>The operating-system process id of the child.</summary>
    public int ProcessId => _processId;

    /// <summary>
    ///     The first non-empty line the child wrote to stderr, or <c>null</c> when it has written none yet.
    ///     A caller that shows it must still truncate it.
    /// </summary>
    /// <remarks>
    ///     <see cref="Task.IsCompleted"/> is also true for a faulted task, and reading
    ///     <see cref="Task{TResult}.Result"/> rethrows its exception. The drain faults whenever the stream is closed
    ///     under it — which is exactly what <see cref="Dispose"/> does — so this asks for successful completion and
    ///     treats a faulted drain as "no line to show" rather than raising an exception nobody can act on.
    /// </remarks>
    public string? FirstErrorLine => _firstErrorLine.IsCompletedSuccessfully ? _firstErrorLine.Result : null;

    /// <summary>Writes one line to the child's standard input and flushes it.</summary>
    /// <param name="line">The line to send, without its terminator.</param>
    public void SendLine(string line)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(line);
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    /// <summary>Reads the child's next standard output line, waiting no longer than the session has left.</summary>
    /// <returns>The line, without its terminator.</returns>
    /// <exception cref="TimeoutException">
    ///     The budget ran out before a line arrived. The session is then unusable: dispose it.
    /// </exception>
    /// <exception cref="EndOfStreamException">The child closed its standard output.</exception>
    /// <exception cref="InvalidDataException">
    ///     The child wrote more than the session's line cap without a terminator. The session is then unusable:
    ///     dispose it.
    /// </exception>
    public string ReadLine()
    {
        ThrowIfDisposed();
        if (_pendingRead is not null)
            throw new InvalidOperationException("A previous read timed out; the session must be disposed, not read again.");

        TimeSpan remaining = _deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("The session budget was exhausted before the child answered.");

        Task<string?> read = ReadLineBoundedAsync();
        try
        {
            // WaitAsync rather than Wait: Wait wraps a faulted reader in AggregateException, and the caller needs
            // the reader's own exception — a line over the cap — to classify it. The wait is clamped because
            // WaitAsync rejects a timeout past the runtime's maximum timer duration, and the budget is a caller's
            // value with no upper bound of its own.
            TimeSpan wait = remaining > MaxWait ? MaxWait : remaining;
            read.WaitAsync(wait).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            // The read is still outstanding on the stream. Keep it as pending so no second read starts
            // while the first one is in flight.
            _pendingRead = read;
            throw new TimeoutException("The child did not answer within the session budget.");
        }

        return read.GetAwaiter().GetResult() ?? throw new EndOfStreamException("The child closed its standard output.");
    }

    /// <summary>
    ///     Reads one line without letting a child that never terminates one grow the buffer past
    ///     <see cref="MaxLineLength"/>. A read may cross a line end, so the remainder is kept for the next call.
    /// </summary>
    private async Task<string?> ReadLineBoundedAsync()
    {
        StringBuilder line = new();
        while (true)
        {
            int newline = _unread.IndexOf('\n', StringComparison.Ordinal);
            if (newline >= 0)
            {
                line.Append(_unread, 0, newline);
                _unread = _unread[(newline + 1)..];
                return Complete(line);
            }

            line.Append(_unread);
            _unread = string.Empty;
            if (line.Length >= MaxLineLength)
                throw LineTooLong();

            char[] buffer = new char[ReadBufferLength];
            int read = await _process.StandardOutput.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
                return line.Length == 0 ? null : Complete(line);

            _unread = new string(buffer, 0, read);
        }
    }

    /// <summary>Trims the line terminator and enforces the cap on a line assembled from more than one read.</summary>
    private static string Complete(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r')
            line.Length--;

        if (line.Length > MaxLineLength)
            throw LineTooLong();

        return line.ToString();
    }

    private static InvalidDataException LineTooLong() =>
        new($"The child wrote more than {MaxLineLength} characters without ending the line.");

    /// <summary>Kills the child's process tree, reaps it, and releases the streams. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Killing the tree is the only point that is guaranteed to end a child that is blocked on stdin or
        // sleeping. It runs whether or not the child has already exited, so no path leaves one behind.
        ProcessRunner.TryKillTree(_process);
        _process.WaitForExit((int)ReapWait.TotalMilliseconds);
        _process.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task<string?> DrainFirstErrorLineAsync()
    {
        // Bounded exactly like the stdout reader. An unbounded ReadLineAsync here would let a child that never
        // writes a terminator grow the reader's buffer until the process runs out of memory, which is the failure
        // the cap exists to prevent on stdout. The over-cap line is dropped rather than truncated — it is not a
        // diagnosis — and the drain keeps reading so the child never blocks on a full stderr pipe.
        string? first = null;
        StringBuilder line = new();
        bool overCap = false;
        char[] buffer = new char[ReadBufferLength];
        try
        {
            while (true)
            {
                int read = await _process.StandardError.ReadAsync(buffer.AsMemory(), CancellationToken.None)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                for (int index = 0; index < read; index++)
                {
                    char current = buffer[index];
                    if (current == '\n')
                    {
                        first ??= CompleteErrorLine(line, overCap);
                        line.Clear();
                        overCap = false;
                        continue;
                    }

                    if (overCap)
                        continue;

                    if (line.Length >= MaxLineLength)
                    {
                        line.Clear();
                        overCap = true;
                        continue;
                    }

                    line.Append(current);
                }
            }

            first ??= CompleteErrorLine(line, overCap);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Dispose closes the stream under this read. A session that is going away has no error line to offer,
            // and an exception from a background drain has no caller that could handle it.
        }

        return first;
    }

    /// <summary>The first non-blank stderr line, or null when the line was blank or discarded for exceeding the cap.</summary>
    private static string? CompleteErrorLine(StringBuilder line, bool overCap)
    {
        if (overCap)
            return null;

        string trimmed = line.ToString().Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }
}
