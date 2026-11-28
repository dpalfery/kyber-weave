using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Data.RunStart;

/// <summary>Runs git as a child process through the kernel's deadlock-safe runner.</summary>
/// <remarks>
/// Every argument goes through <see cref="ProcessStartInfo.ArgumentList"/>, never a concatenated string, and every child is
/// bounded by <paramref name="timeout"/>, so a hung remote cannot hold a run open.
/// </remarks>
/// <param name="timeout">The longest a git child may run before its process tree is killed.</param>
public sealed class ProcessGitCommandRunner(TimeSpan timeout) : IGitCommandRunner
{
    private const string GitExecutable = "git";

    // Resolved once per runner, not per child: walking the PATH is file-system work no git call should repeat, and
    // a full path keeps the child launch independent of the directory this process happens to sit in. The bare name
    // stays as the fallback for a launcher that resolves git in a way FindOnPath cannot see.
    private readonly string _gitExecutable = ProcessRunner.FindOnPath(GitExecutable) ?? GitExecutable;

    /// <summary>The bound one run-start git child gets by default: a fetch to a slow remote is the only one expected to take real time.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <inheritdoc />
    public GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo startInfo = new(_gitExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, timeout);
        return new GitOutcome(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    /// <inheritdoc />
    public async Task<GitOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo startInfo = new(_gitExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = await ProcessRunner.RunAsync(
            startInfo,
            string.Empty,
            timeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new GitOutcome(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}
