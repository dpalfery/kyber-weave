using System.Diagnostics;

namespace KyberWeave.Core.Processes;

/// <summary>What a finished child process wrote, and how it ended.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Everything written to stdout.</param>
/// <param name="StandardError">Everything written to stderr.</param>
public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs a child process to completion without deadlocking on its pipes.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Drains both redirected streams, waits for exit, and returns what was captured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Draining both pipes concurrently is required, not tidy. An OS pipe buffer is finite
    /// — commonly 64 KB — so a child that fills the stream the parent is not currently
    /// reading blocks on write. The parent is meanwhile blocked reading the other stream,
    /// which the blocked child will never close. Neither side can proceed.
    /// </para>
    /// <para>
    /// Both of the obvious spellings hit this: reading one stream to the end and then the
    /// other, and calling <see cref="Process.WaitForExit()"/> before draining either. Both
    /// survive testing because test fixtures are quiet, then hang on the one real input
    /// that is talkative. Route every redirected process through here.
    /// </para>
    /// </remarks>
    /// <param name="process">A started process with both output streams redirected.</param>
    public static ProcessResult ReadToEnd(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // Both reads are started before either is awaited, so neither pipe can fill while
        // the other is being consumed.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        var captured = Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();

        // Safe only now: both streams are at EOF, so the child cannot be blocked on write.
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, captured[0], captured[1]);
    }
}
