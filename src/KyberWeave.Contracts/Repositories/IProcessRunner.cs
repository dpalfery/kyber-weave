namespace KyberWeave.Contracts.Repositories;

/// <summary>Runs a command to completion.</summary>
public interface IProcessRunner
{
    /// <summary>Runs a command and waits for it.</summary>
    /// <param name="argv">The program followed by its arguments; never passed through a shell.</param>
    /// <param name="workingDirectory">The directory to run in.</param>
    /// <param name="timeout">Longest the command may run before its process tree is killed.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code and merged output. Throws when the program cannot be started.</returns>
    Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
