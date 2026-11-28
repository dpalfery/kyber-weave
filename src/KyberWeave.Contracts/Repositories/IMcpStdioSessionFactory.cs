namespace KyberWeave.Contracts.Repositories;

/// <summary>Starts an MCP server child and talks to it over its standard input and output.</summary>
public interface IMcpStdioSessionFactory
{
    /// <summary>Starts the server.</summary>
    /// <param name="executable">The executable, resolved through PATH.</param>
    /// <param name="arguments">The arguments, never passed through a shell.</param>
    /// <param name="budget">The total time the whole session may take.</param>
    /// <returns>The session; disposing it kills the child's process tree.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable cannot be started.</exception>
    IMcpStdioSession Start(string executable, IReadOnlyList<string> arguments, TimeSpan budget);
}

/// <summary>One running MCP server child.</summary>
public interface IMcpStdioSession : IDisposable
{
    /// <summary>Gets the first line the child wrote to standard error, or null when none has arrived.</summary>
    string? FirstErrorLine { get; }

    /// <summary>Gets <see cref="FirstErrorLine"/> after waiting up to <paramref name="wait"/> for standard error to finish.</summary>
    /// <param name="wait">The longest to wait.</param>
    /// <returns>The first error line, or null.</returns>
    string? WaitForFirstErrorLine(TimeSpan wait);

    /// <summary>Gets <see cref="FirstErrorLine"/> after waiting up to <paramref name="wait"/> for standard error to finish, without blocking a thread.</summary>
    /// <param name="wait">The longest to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The first error line, or null.</returns>
    Task<string?> WaitForFirstErrorLineAsync(TimeSpan wait, CancellationToken cancellationToken = default);

    /// <summary>Writes one line to the child's standard input and flushes it.</summary>
    /// <param name="line">The line, without its terminator.</param>
    void SendLine(string line);

    /// <summary>Reads the child's next standard output line, waiting no longer than the session has left.</summary>
    /// <returns>The line, without its terminator.</returns>
    /// <exception cref="TimeoutException">The budget ran out before a line arrived.</exception>
    /// <exception cref="EndOfStreamException">The child closed its standard output.</exception>
    string ReadLine();

    /// <summary>Reads the child's next standard output line without blocking a thread, waiting no longer than the session has left.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The line, without its terminator.</returns>
    /// <exception cref="TimeoutException">The budget ran out before a line arrived.</exception>
    /// <exception cref="EndOfStreamException">The child closed its standard output.</exception>
    Task<string> ReadLineAsync(CancellationToken cancellationToken = default);
}
