using System.ComponentModel;
using System.Diagnostics;

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

    private readonly Process _process;
    private readonly int _processId;
    private readonly DateTime _deadline;
    private readonly Task<string?> _firstErrorLine;
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
    public string? FirstErrorLine => _firstErrorLine.IsCompleted ? _firstErrorLine.Result : null;

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
    public string ReadLine()
    {
        ThrowIfDisposed();
        if (_pendingRead is not null)
            throw new InvalidOperationException("A previous read timed out; the session must be disposed, not read again.");

        TimeSpan remaining = _deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("The session budget was exhausted before the child answered.");

        Task<string?> read = _process.StandardOutput.ReadLineAsync();
        if (!read.Wait(remaining))
        {
            // The read is still outstanding on the stream. Keep it as pending so no second read starts
            // while the first one is in flight.
            _pendingRead = read;
            throw new TimeoutException("The child did not answer within the session budget.");
        }

        return read.Result ?? throw new EndOfStreamException("The child closed its standard output.");
    }

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
        string? first = null;
        while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (first is null && line.Trim() is { Length: > 0 } trimmed)
                first = trimmed;
        }

        return first;
    }
}
