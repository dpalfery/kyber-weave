using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using KyberDash.Core.Exceptions;

namespace KyberDash.Core.Processes;

/// <summary>What a finished child process wrote, and how it ended.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="StandardOutput">Everything written to stdout.</param>
/// <param name="StandardError">Everything written to stderr.</param>
public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs a child process to completion without deadlocking on its pipes.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Starts a child process, transfers its standard input, and captures its output without blocking the caller.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string standardInput,
        TimeSpan? timeout = null,
        long? maxOutputBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardInput);

        if (startInfo.UseShellExecute ||
            !startInfo.RedirectStandardInput ||
            !startInfo.RedirectStandardOutput ||
            !startInfo.RedirectStandardError)
        {
            throw new ArgumentException(
                "Standard input, standard output, and standard error must be redirected with shell execution disabled.",
                nameof(startInfo));
        }

        if (!string.IsNullOrEmpty(startInfo.Arguments))
            throw new ArgumentException("Pass arguments through ArgumentList, not Arguments.", nameof(startInfo));

        using Process process = Process.Start(CreateSafeStartInfo(startInfo))
                                ?? throw new InvalidOperationException("The child process could not be started.");

        Task<string> standardOutput = ReadBoundedAsync(process.StandardOutput, maxOutputBytes);
        Task<string> standardError = ReadBoundedAsync(process.StandardError, maxOutputBytes);
        Task inputWrite = WriteAndCloseAsync(process.StandardInput, standardInput);

        bool timedOut = false;
        try
        {
            Task io = Task.WhenAll(inputWrite, standardOutput, standardError);
            if (timeout is { } limit)
            {
                using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(limit);
                Task timer = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                Task completed = await Task.WhenAny(io, timer).ConfigureAwait(false);
                // A child that finished as the timer fired has finished: only an unfinished run times out.
                if (completed != io && !io.IsCompleted)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        timedOut = true;
                        TryKillTree(process);
                        await Task.WhenAny(io, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
                        throw new TimeoutException(
                            $"The process did not exit within {limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");
                    }

                    // The caller's token fired before the timeout; surface cancellation like the blocking wait does.
                    throw new OperationCanceledException(cancellationToken);
                }

                await io.ConfigureAwait(false);
            }
            else if (cancellationToken.CanBeCanceled)
            {
                Task timer = Task.Delay(Timeout.Infinite, cancellationToken);
                Task completed = await Task.WhenAny(io, timer).ConfigureAwait(false);
                if (completed != io && !io.IsCompleted)
                    throw new OperationCanceledException(cancellationToken);

                await io.ConfigureAwait(false);
            }
            else
            {
                await io.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            TryKillTree(process);
            if (ex is AggregateException ae)
                throw ae.Flatten().InnerExceptions[0];
            throw;
        }
        finally
        {
            if (timedOut || !process.HasExited)
            {
                using CancellationTokenSource graceCts = new(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            else
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    /// <summary>
    /// Runs an interactive or long-lived child process, streaming standard output and standard error
    /// as chunks arrive and killing the child process tree on cancellation.
    /// </summary>
    /// <param name="startInfo">Child launch settings using <see cref="ProcessStartInfo.ArgumentList"/>.</param>
    /// <param name="output">Writer receiving child standard output chunks immediately.</param>
    /// <param name="standardError">Writer receiving child standard error chunks immediately.</param>
    /// <param name="forwardStandardInput">
    /// <see langword="true"/> to inherit the parent process standard input; <see langword="false"/> to
    /// close the child's standard input immediately.
    /// </param>
    /// <param name="cancellationToken">Stops the child process tree cleanly when canceled.</param>
    /// <returns>The child exit code, or <c>0</c> when stopped by cancellation.</returns>
    public static async Task<int> RunInteractiveAsync(
        ProcessStartInfo startInfo,
        TextWriter output,
        TextWriter standardError,
        bool forwardStandardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(standardError);

        if (startInfo.UseShellExecute)
            throw new ArgumentException("Shell execution must be disabled.", nameof(startInfo));

        if (!string.IsNullOrEmpty(startInfo.Arguments))
            throw new ArgumentException("Pass arguments through ArgumentList, not Arguments.", nameof(startInfo));

        if (cancellationToken.IsCancellationRequested)
            return 0;

        ProcessStartInfo safe = CreateSafeStartInfo(startInfo);
        safe.RedirectStandardInput = !forwardStandardInput;
        safe.RedirectStandardOutput = true;
        safe.RedirectStandardError = true;

        using Process process = Process.Start(safe)
                                ?? throw new InvalidOperationException("The child process could not be started.");

        if (!forwardStandardInput)
            process.StandardInput.Close();

        Task stdoutPump = StreamChunksAsync(process.StandardOutput, output);
        Task stderrPump = StreamChunksAsync(process.StandardError, standardError);

        await using CancellationTokenRegistration cancelRegistration = cancellationToken.Register(() => TryKillTree(process));
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested ? 0 : process.ExitCode;
        }
        finally
        {
            TryKillTree(process);
            if (!process.HasExited)
            {
                try
                {
                    using CancellationTokenSource waitTimeout = new(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(waitTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
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
        }
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
            StandardErrorEncoding = startInfo.StandardErrorEncoding,
        };

        foreach (string argument in startInfo.ArgumentList)
            safe.ArgumentList.Add(argument);

        safe.Environment.Clear();
        foreach (KeyValuePair<string, string?> pair in startInfo.Environment)
        {
            if (pair.Value is not null)
                safe.Environment[pair.Key] = pair.Value;
        }

        return safe;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, long? maxBytes)
    {
        if (maxBytes is null)
            return await reader.ReadToEndAsync().ConfigureAwait(false);

        if (maxBytes.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes.Value, "maxBytes cannot be negative.");

        long max = maxBytes.Value;
        int bufferSize = (int)Math.Clamp(max > 16383 ? 16384 : max + 1, 1, 16384);
        char[] buffer = new char[bufferSize];
        StringBuilder sb = new();
        long totalBytes = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            totalBytes += reader.CurrentEncoding.GetByteCount(buffer, 0, read);
            if (totalBytes > max)
                throw new OutputLimitExceededException($"The process output exceeded the limit of {max.ToString(CultureInfo.InvariantCulture)} bytes.");

            sb.Append(buffer, 0, read);
        }

        return sb.ToString();
    }

    private static async Task StreamChunksAsync(StreamReader reader, TextWriter destination)
    {
        char[] buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            await destination.FlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task WriteAndCloseAsync(StreamWriter writer, string input)
    {
        try
        {
            await writer.WriteAsync(input).ConfigureAwait(false);
        }
        finally
        {
            writer.Close();
        }
    }
}
