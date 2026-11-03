using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

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
    ///     Starts a child process, transfers its standard input, and captures its output.
    /// </summary>
    /// <remarks>
    ///     The output reads begin before input is written. A child is allowed to fill its
    ///     output pipes before reading stdin, so writing all input first can deadlock just as
    ///     surely as draining stdout and stderr sequentially can. Closing stdin after the
    ///     write is equally important: many command-line tools do not proceed until EOF.
    /// </remarks>
    /// <param name="startInfo">
    ///     Process configuration with stdin, stdout, and stderr redirected, shell execution
    ///     disabled, and arguments supplied through <see cref="ProcessStartInfo.ArgumentList" />
    ///     rather than the concatenated <see cref="ProcessStartInfo.Arguments" /> string.
    /// </param>
    /// <param name="standardInput">The complete text to write to the child process.</param>
    /// <param name="timeout">
    ///     When set, the process tree is killed and a <see cref="TimeoutException" /> is thrown
    ///     if the child has not finished within this limit. Unbounded waits are how a hung gate
    ///     takes the whole review with it.
    /// </param>
    public static ProcessResult Run(ProcessStartInfo startInfo, string standardInput, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);

        if (startInfo.UseShellExecute ||
            !startInfo.RedirectStandardInput ||
            !startInfo.RedirectStandardOutput ||
            !startInfo.RedirectStandardError)
            throw new ArgumentException(
                "Standard input, standard output, and standard error must be redirected " +
                "with shell execution disabled.",
                nameof(startInfo));

        if (!string.IsNullOrEmpty(startInfo.Arguments))
            throw new ArgumentException(
                "Pass arguments through ArgumentList, not Arguments.",
                nameof(startInfo));

        // The caller's object is not started as-is. Arguments is a single string a shell
        // would re-parse; ArgumentList is argv. A fresh start info carries only the file
        // name, working directory, and argument list, with the shell kept off, so neither
        // a concatenated command string nor UseShellExecute can reach Process.Start.
        using Process process = Process.Start(CreateSafeStartInfo(startInfo))
                                ?? throw new InvalidOperationException("The child process could not be started.");

        // Reads start before the write because a child may produce more than one pipe
        // buffer of output before it consumes any stdin.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        Task inputWrite = WriteAndCloseAsync(process.StandardInput, standardInput);

        bool timedOut = false;
        try
        {
            Task io = Task.WhenAll(inputWrite, standardOutput, standardError);
            if (timeout is { } limit)
            {
                if (!io.Wait(limit))
                {
                    timedOut = true;
                    TryKillTree(process);
                    io.Wait(TimeSpan.FromSeconds(5));
                    throw new TimeoutException(
                        $"The process did not exit within {limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");
                }
            }
            else
            {
                io.GetAwaiter().GetResult();
            }
        }
        finally
        {
            // After a timeout kill, an unbounded WaitForExit can hang if the tree did not
            // actually die. The success path still waits without a cap: WhenAll already
            // drained both pipes, so the child cannot be blocked on write.
            if (timedOut)
                process.WaitForExit(TimeSpan.FromSeconds(5));
            else
                process.WaitForExit();
        }

        return new ProcessResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    internal static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone, or the OS refused the kill. The timeout still reports as a failure.
        }
    }

    /// <summary>
    ///     Drains both redirected streams, waits for exit, and returns what was captured.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Draining both pipes concurrently is required, not tidy. An OS pipe buffer is finite
    ///         — commonly 64 KB — so a child that fills the stream the parent is not currently
    ///         reading blocks on write. The parent is meanwhile blocked reading the other stream,
    ///         which the blocked child will never close. Neither side can proceed.
    ///     </para>
    ///     <para>
    ///         Both of the obvious spellings hit this: reading one stream to the end and then the
    ///         other, and calling <see cref="Process.WaitForExit()" /> before draining either. Both
    ///         survive testing because test fixtures are quiet, then hang on the one real input
    ///         that is talkative. Route every redirected process through here.
    ///     </para>
    /// </remarks>
    /// <param name="process">A started process with both output streams redirected.</param>
    public static ProcessResult ReadToEnd(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // Both reads are started before either is awaited, so neither pipe can fill while
        // the other is being consumed.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        string[] captured = Task.WhenAll(standardOutput, standardError).GetAwaiter().GetResult();

        // Safe only now: both streams are at EOF, so the child cannot be blocked on write.
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, captured[0], captured[1]);
    }

    /// <summary>
    ///     Runs a child process interactively without standard stream redirection.
    /// </summary>
    /// <remarks>
    ///     Interactive execution connects the child process directly to the active console streams
    ///     (stdin, stdout, stderr) without pipe redirection, allowing full terminal interactivity.
    ///     Shell execution is explicitly disabled.
    /// </remarks>
    /// <param name="startInfo">The process configuration to execute.</param>
    /// <returns>The exit code returned by the finished process.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="startInfo" /> is null.</exception>
    public static int RunInteractive(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = false;
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;

        // UseShellExecute is explicitly disabled above, and upstream callers (such as
        // SquadHarnessLauncher.BuildStartInfo) validate executables and arguments
        // against command-injection characters before launch.
        // nosemgrep: csharp.lang.security.injections.os-command.os-command-injection
        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("The child process could not be started.");

        process.WaitForExit();
        return process.ExitCode;
    }

    internal static ProcessStartInfo CreateSafeStartInfo(ProcessStartInfo startInfo)
    {
        ProcessStartInfo safe = new(startInfo.FileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = startInfo.WorkingDirectory,
            CreateNoWindow = startInfo.CreateNoWindow,
            StandardInputEncoding = startInfo.StandardInputEncoding,
            StandardOutputEncoding = startInfo.StandardOutputEncoding,
            StandardErrorEncoding = startInfo.StandardErrorEncoding
        };

        foreach (string argument in startInfo.ArgumentList)
            safe.ArgumentList.Add(argument);

        // A fresh ProcessStartInfo inherits the current environment. Clear and copy so a
        // caller that removed or overrode variables keeps that view, without re-enabling
        // the shell or the concatenated Arguments string.
        safe.Environment.Clear();
        foreach (KeyValuePair<string, string?> pair in startInfo.Environment)
            if (pair.Value is not null)
                safe.Environment[pair.Key] = pair.Value;

        return safe;
    }

    private static async Task WriteAndCloseAsync(StreamWriter writer, string input)
    {
        try
        {
            await writer.WriteAsync(input).ConfigureAwait(false);
        }
        finally
        {
            // Close communicates EOF even for empty input. It also flushes any text still
            // buffered by StreamWriter, so failures during that final transfer propagate.
            writer.Close();
        }
    }
}
