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
    /// Starts a child process, transfers its standard input, and captures its output.
    /// </summary>
    /// <remarks>
    /// The output reads begin before input is written. A child is allowed to fill its
    /// output pipes before reading stdin, so writing all input first can deadlock just as
    /// surely as draining stdout and stderr sequentially can. Closing stdin after the
    /// write is equally important: many command-line tools do not proceed until EOF.
    /// </remarks>
    /// <param name="startInfo">
    /// Process configuration with stdin, stdout, and stderr redirected, shell execution
    /// disabled, and arguments supplied through <see cref="ProcessStartInfo.ArgumentList"/>
    /// rather than the concatenated <see cref="ProcessStartInfo.Arguments"/> string.
    /// </param>
    /// <param name="standardInput">The complete text to write to the child process.</param>
    public static ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);

        if (startInfo.UseShellExecute ||
            !startInfo.RedirectStandardInput ||
            !startInfo.RedirectStandardOutput ||
            !startInfo.RedirectStandardError)
        {
            throw new ArgumentException(
                "Standard input, standard output, and standard error must be redirected " +
                "with shell execution disabled.",
                nameof(startInfo));
        }

        if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            throw new ArgumentException(
                "Pass arguments through ArgumentList, not Arguments.",
                nameof(startInfo));
        }

        // The caller's object is not started as-is. Arguments is a single string a shell
        // would re-parse; ArgumentList is argv. A fresh start info carries only the file
        // name, working directory, and argument list, with the shell kept off, so neither
        // a concatenated command string nor UseShellExecute can reach Process.Start.
        using var process = Process.Start(CreateSafeStartInfo(startInfo))
            ?? throw new InvalidOperationException("The child process could not be started.");

        // Reads start before the write because a child may produce more than one pipe
        // buffer of output before it consumes any stdin.
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var inputWrite = WriteAndCloseAsync(process.StandardInput, standardInput);

        try
        {
            Task.WhenAll(inputWrite, standardOutput, standardError).GetAwaiter().GetResult();
        }
        finally
        {
            // WhenAll does not finish until both output streams reach EOF, so waiting here
            // cannot leave the child blocked on a full redirected pipe.
            process.WaitForExit();
        }

        return new ProcessResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

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

    private static ProcessStartInfo CreateSafeStartInfo(ProcessStartInfo startInfo)
    {
        var safe = new ProcessStartInfo(startInfo.FileName)
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

        foreach (var argument in startInfo.ArgumentList)
            safe.ArgumentList.Add(argument);

        // A fresh ProcessStartInfo inherits the current environment. Clear and copy so a
        // caller that removed or overrode variables keeps that view, without re-enabling
        // the shell or the concatenated Arguments string.
        safe.Environment.Clear();
        foreach (var pair in startInfo.Environment)
        {
            if (pair.Value is not null)
                safe.Environment[pair.Key] = pair.Value;
        }

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
