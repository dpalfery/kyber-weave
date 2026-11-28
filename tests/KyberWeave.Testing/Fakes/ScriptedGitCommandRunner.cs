using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Tests.Fakes;

/// <summary>A git runner a test scripts, recording every call instead of starting git.</summary>
/// <remarks>
/// A test answers by working directory and argv, so the same command can differ between the task and the run
/// worktree. The working directory is recorded as the caller passed it and the arguments exactly as they were
/// given, <c>-C</c> included.
/// </remarks>
internal sealed class ScriptedGitCommandRunner : IGitCommandRunner
{
    private readonly Func<string, IReadOnlyList<string>, GitOutcome> _respond;

    /// <summary>Creates a runner whose every call is answered by <paramref name="respond"/>.</summary>
    /// <param name="respond">Answers one invocation: the working directory and argv git would receive.</param>
    public ScriptedGitCommandRunner(Func<string, IReadOnlyList<string>, GitOutcome> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _respond = respond;
    }

    /// <summary>Every invocation, in order.</summary>
    public List<(string WorkingDirectory, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    /// <inheritdoc />
    public GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        Calls.Add((workingDirectory, [.. arguments]));
        return _respond(workingDirectory, arguments);
    }

    /// <inheritdoc />
    public Task<GitOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(Run(workingDirectory, arguments));

    /// <summary>Whether any invocation carried this exact argv.</summary>
    /// <param name="arguments">The argv to look for.</param>
    public bool Called(params string[] arguments) =>
        Calls.Any(call => call.Arguments.SequenceEqual(arguments, StringComparer.Ordinal));
}
