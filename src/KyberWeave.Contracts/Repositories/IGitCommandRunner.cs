using KyberWeave.Core.RunStart;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Runs one git command to completion, keeping standard output and standard error apart.</summary>
public interface IGitCommandRunner
{
    /// <summary>Runs git with the given arguments.</summary>
    /// <param name="workingDirectory">The directory git runs in.</param>
    /// <param name="arguments">One element per argument; never passed through a shell.</param>
    /// <returns>The exit code and both output streams. Throws when git cannot be started or outlives its time limit.</returns>
    GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments);

    /// <summary>Runs git with the given arguments, without blocking a thread while it runs.</summary>
    /// <param name="workingDirectory">The directory git runs in.</param>
    /// <param name="arguments">One element per argument; never passed through a shell.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process tree.</param>
    /// <returns>The exit code and both output streams. Throws when git cannot be started or outlives its time limit.</returns>
    Task<GitOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}
