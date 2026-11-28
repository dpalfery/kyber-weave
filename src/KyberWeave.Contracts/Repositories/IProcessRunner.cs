using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The one process seam: start a program, ask about a running process, and find a program on the path.</summary>
/// <remarks>
/// <para>
/// A scripted fake implements this interface and is proven against the real adapter by one shared contract suite.
/// </para>
/// </remarks>
public interface IProcessRunner
{
    /// <summary>Runs a program to completion and captures its output.</summary>
    /// <param name="request">The program, its arguments, and the conditions of the run.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code, both output streams and whether the run timed out.</returns>
    /// <exception cref="FileNotFoundException">The program was not found.</exception>
    /// <exception cref="DirectoryNotFoundException">The working directory was not found.</exception>
    /// <exception cref="OutputLimitExceededException">The program wrote more than <see cref="ProcessRunRequest.MaxOutputBytes"/>.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether a process with this id is still running.</summary>
    /// <param name="processId">The process id, as the operating system reports it.</param>
    /// <returns>True when the process is running; false when it has ended, never existed or the id is not valid.</returns>
    bool IsAlive(int processId);

    /// <summary>Finds a program the way the operating system does.</summary>
    /// <param name="program">A program name to look up on the path, or a path to check.</param>
    /// <returns>The full path of the program, or null when it is not there.</returns>
    string? FindOnPath(string program);

    /// <summary>Runs a command given as an argv list and waits for it.</summary>
    /// <param name="argv">The program followed by its arguments; never passed through a shell.</param>
    /// <param name="workingDirectory">The directory to run in.</param>
    /// <param name="timeout">Longest the command may run before its process tree is killed.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code and merged output. Throws <see cref="FileNotFoundException"/> ("command not found: name") when the program does not exist, and another exception when it cannot be started.</returns>
    async Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
        {
            throw new ArgumentException("A command needs at least a program name.", nameof(argv));
        }

        ProcessRunRequest request = new()
        {
            Program = argv[0],
            Arguments = [.. argv.Skip(1)],
            WorkingDirectory = workingDirectory,
            Timeout = timeout,
            MaxOutputBytes = ProcessRunRequest.DefaultMaxOutputBytes
        };

        try
        {
            return await RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OutputLimitExceededException exception)
        {
            return new ProcessRunResult(-1, exception.Message, TimedOut: false);
        }
    }
}

/// <summary>The asynchronous twins of the role views over <see cref="IProcessRunner"/>.</summary>
/// <remarks>
/// Each awaits <see cref="IProcessRunner.RunAsync(ProcessRunRequest, CancellationToken)"/> and keeps the exception
/// contract of the synchronous role view it twins: a program that could not be started is a
/// <see cref="Win32Exception"/>, and a run killed for running too long is a <see cref="TimeoutException"/>. A caller
/// moving to these methods keeps every catch clause it had.
/// </remarks>
public static class ProcessRunnerAsyncViews
{
    /// <summary>Runs a configured process to completion without blocking a thread.</summary>
    /// <param name="runner">The process seam to run through.</param>
    /// <param name="startInfo">The configured program; its argument list is copied, never its argument string.</param>
    /// <param name="standardInput">The text written to standard input.</param>
    /// <param name="timeout">The longest the process may run; null for no limit.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code and both output streams.</returns>
    /// <exception cref="Win32Exception">The program could not be started.</exception>
    /// <exception cref="TimeoutException">The program outlived <paramref name="timeout"/>.</exception>
    public static async Task<ProcessResult> RunAsync(
        this IProcessRunner runner,
        ProcessStartInfo startInfo,
        string standardInput,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);

        ProcessRunRequest request = ProcessRunRequest.FromStartInfo(startInfo, standardInput, timeout);
        return await ToProcessResultAsync(runner, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a program with argv to completion without blocking a thread.</summary>
    /// <param name="runner">The process seam to run through.</param>
    /// <param name="program">The program to start.</param>
    /// <param name="arguments">One element per argument.</param>
    /// <param name="workingDirectory">The directory to run in.</param>
    /// <param name="timeout">Longest the program may run before its process tree is killed; null for no limit.</param>
    /// <param name="maxOutputBytes">When set, the most bytes of output read before the process tree is killed.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code and both output streams.</returns>
    /// <exception cref="Win32Exception">The program could not be started.</exception>
    /// <exception cref="TimeoutException">The program outlived <paramref name="timeout"/>.</exception>
    public static async Task<ProcessResult> RunAsync(
        this IProcessRunner runner,
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan? timeout,
        long? maxOutputBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);

        ProcessRunRequest request = new()
        {
            Program = program,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Timeout = timeout,
            MaxOutputBytes = maxOutputBytes
        };

        return await ToProcessResultAsync(runner, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one request and translates its result the way the synchronous role views do.</summary>
    /// <param name="runner">The process seam to run through.</param>
    /// <param name="request">The program, its arguments, and the conditions of the run.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code and both output streams.</returns>
    private static async Task<ProcessResult> ToProcessResultAsync(
        IProcessRunner runner,
        ProcessRunRequest request,
        CancellationToken cancellationToken)
    {
        ProcessRunResult result;
        try
        {
            result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // These role views have always reported a start failure as Win32Exception, and their callers
            // tell "the program is not installed" apart from a timeout by catching exactly that type.
            throw new Win32Exception(2, exception.Message);
        }

        if (result.TimedOut)
        {
            throw new TimeoutException(TimeoutMessage(request.Timeout));
        }

        return new ProcessResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static string TimeoutMessage(TimeSpan? timeout) =>
        timeout is { } limit
            ? $"The process did not exit within {limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds."
            : "The process did not exit in time.";
}
