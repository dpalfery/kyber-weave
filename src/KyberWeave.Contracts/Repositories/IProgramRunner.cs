using KyberWeave.Core.Processes;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Starts a program and waits for it, keeping its standard output and standard error apart.</summary>
/// <remarks>
/// Unlike <see cref="IProcessRunner"/>, this keeps the two streams separate and lets the original exception through, because a
/// review gate reports a timeout, a program that could not start, and a stopped run each in its own words.
/// </remarks>
public interface IProgramRunner
{
    /// <summary>Runs a program with argv and never a shell.</summary>
    /// <param name="program">The program to start.</param>
    /// <param name="arguments">One element per argument.</param>
    /// <param name="workingDirectory">The directory to run in.</param>
    /// <param name="timeout">Longest the program may run before its process tree is killed; null for no limit.</param>
    /// <param name="maxOutputBytes">When set, the most bytes of output read before the process tree is killed.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code and both output streams.</returns>
    /// <exception cref="TimeoutException">The program outlived <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started.</exception>
    ProcessResult Run(
        string program,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        long? maxOutputBytes = null,
        CancellationToken cancellationToken = default);
}
