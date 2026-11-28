using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;

namespace KyberWeave.Core.Processes;

/// <summary>Thrown when a child process produces more output than its configured byte budget.</summary>
public sealed class OutputLimitExceededException : InvalidOperationException
{
    public OutputLimitExceededException() { }
    public OutputLimitExceededException(string? message) : base(message) { }
    public OutputLimitExceededException(string? message, Exception? innerException) : base(message, innerException) { }
}

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
    /// <param name="maxOutputBytes">
    ///     When set, bounds standard output and standard error reading to this many bytes. If the child process
    ///     produces more output, its process tree is killed and an <see cref="OutputLimitExceededException" />
    ///     is thrown before the full output is buffered in memory.
    /// </param>
    /// <param name="cancellationToken">
    ///     When cancelled, the process tree is killed and an <see cref="OperationCanceledException" /> is thrown.
    /// </param>
    public static ProcessResult Run(
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
        using Process process = StartRetryingTextFileBusy(CreateSafeStartInfo(startInfo));

        // Reads start before the write because a child may produce more than one pipe
        // buffer of output before it consumes any stdin. The three transfers run on their
        // own threads: this is the synchronous adapter, so it waits on threads rather than
        // blocking on a Task.
        string standardOutput = string.Empty;
        string standardError = string.Empty;
        Exception? ioFailure = null;
        using ManualResetEventSlim ioDone = new(false);
        int remaining = 3;

        void Complete()
        {
            if (Interlocked.Decrement(ref remaining) == 0)
            {
                try
                {
                    ioDone.Set();
                }
                catch (ObjectDisposedException)
                {
                    // The timeout path already gave the killed tree its bounded wait and returned; a reader
                    // that only then reaches EOF must not fault a background thread.
                }
            }
        }

        void Capture(Exception ex) => Interlocked.CompareExchange(ref ioFailure, ex, null);

        Thread outputReader = new(() =>
            ReadCaptured(process.StandardOutput, maxOutputBytes, text => standardOutput = text, Capture, Complete))
        { IsBackground = true };

        Thread errorReader = new(() =>
            ReadCaptured(process.StandardError, maxOutputBytes, text => standardError = text, Capture, Complete))
        { IsBackground = true };

        Thread inputWriter = new(() =>
            WriteCaptured(process.StandardInput, standardInput, Capture, Complete))
        { IsBackground = true };

        outputReader.Start();
        errorReader.Start();
        inputWriter.Start();

        bool timedOut = false;
        try
        {
            if (timeout is { } limit)
            {
                if (!ioDone.Wait(limit, cancellationToken))
                {
                    timedOut = true;
                    TryKillTree(process);
                    _ = ioDone.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
                    throw new TimeoutException(
                        $"The process did not exit within {limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");
                }
            }
            else if (cancellationToken.CanBeCanceled)
            {
                ioDone.Wait(cancellationToken);
            }
            else
            {
                ioDone.Wait(Timeout.Infinite, CancellationToken.None);
            }

            if (ioFailure is not null)
            {
                ExceptionDispatchInfo.Capture(ioFailure).Throw();
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
            // After a timeout kill or fault, an unbounded WaitForExit can hang if the tree did not
            // actually die. The success path still waits without a cap: the readers already
            // drained both pipes, so the child cannot be blocked on write.
            if (timedOut || !process.HasExited)
                process.WaitForExit(TimeSpan.FromSeconds(5));
            else
                process.WaitForExit();
        }

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    /// <summary>
    ///     Starts a child process, transfers its standard input, and captures its output without blocking a thread.
    /// </summary>
    /// <remarks>
    ///     This is the asynchronous twin of <see cref="Run" />: it starts both output reads before writing input,
    ///     enforces the same timeout and output budget, and kills the process tree on every failure path. A missing
    ///     program still surfaces as the platform's start failure, and a timeout still surfaces as a
    ///     <see cref="TimeoutException" /> with the same message.
    /// </remarks>
    /// <param name="startInfo">
    ///     Process configuration with stdin, stdout, and stderr redirected, shell execution
    ///     disabled, and arguments supplied through <see cref="ProcessStartInfo.ArgumentList" />
    ///     rather than the concatenated <see cref="ProcessStartInfo.Arguments" /> string.
    /// </param>
    /// <param name="standardInput">The complete text to write to the child process.</param>
    /// <param name="timeout">
    ///     When set, the process tree is killed and a <see cref="TimeoutException" /> is thrown
    ///     if the child has not finished within this limit.
    /// </param>
    /// <param name="maxOutputBytes">
    ///     When set, bounds standard output and standard error reading to this many bytes. If the child process
    ///     produces more output, its process tree is killed and an <see cref="OutputLimitExceededException" />
    ///     is thrown before the full output is buffered in memory.
    /// </param>
    /// <param name="cancellationToken">
    ///     When cancelled, the process tree is killed and an <see cref="OperationCanceledException" /> is thrown.
    /// </param>
    /// <param name="time">The clock the timeout delay is measured on; null uses the system clock.</param>
    /// <returns>The finished process's exit code and captured streams.</returns>
    public static async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string standardInput,
        TimeSpan? timeout = null,
        long? maxOutputBytes = null,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
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

        TimeProvider clock = time ?? TimeProvider.System;
        using Process process = StartRetryingTextFileBusy(CreateSafeStartInfo(startInfo));

        // Reads start before the write for the same reason as the synchronous twin: a child may
        // produce more than one pipe buffer of output before it consumes any stdin.
        Task<string> standardOutput = ReadBoundedAsync(process.StandardOutput, maxOutputBytes);
        Task<string> standardError = ReadBoundedAsync(process.StandardError, maxOutputBytes);
        Task inputWrite = WriteAndCloseAsync(process.StandardInput, standardInput);
        Task io = Task.WhenAll(inputWrite, standardOutput, standardError);

        bool timedOut = false;
        try
        {
            if (timeout is { } limit)
            {
                Task delay = Task.Delay(limit, clock, cancellationToken);
                Task completed = await Task.WhenAny(io, delay).ConfigureAwait(false);
                if (completed == delay && !io.IsCompleted)
                {
                    // The delay either expired or the caller cancelled. Awaiting it surfaces the
                    // cancellation; an expiry falls through to the same kill-and-report path as Run.
                    await delay.ConfigureAwait(false);
                    timedOut = true;
                    TryKillTree(process);
                    await WaitForIoBoundedAsync(io).ConfigureAwait(false);
                    throw new TimeoutException(
                        $"The process did not exit within {limit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.");
                }

                await io.ConfigureAwait(false);
            }
            else if (cancellationToken.CanBeCanceled)
            {
                await io.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            // After a timeout kill or fault, an unbounded wait for exit can hang if the tree did not
            // actually die. The success path still waits without a cap: WhenAll already drained both
            // pipes, so the child cannot be blocked on write.
            if (timedOut || !process.HasExited)
            {
                using CancellationTokenSource reap = new(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(reap.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The killed tree did not reap within the same five-second bound as Run.
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

    /// <summary>Whether a process with this id is still running.</summary>
    /// <param name="processId">The process id, as the operating system reports it.</param>
    /// <returns>True when the process is running; false when it has ended, never existed or the id is not valid.</returns>
    internal static bool IsAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Finds a program the way the operating system's process launcher does.</summary>
    /// <param name="program">A program name to look up on the path, or a path to check.</param>
    /// <returns>The full path of the program, or null when it is not there.</returns>
    internal static string? FindOnPath(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        if (ContainsDirectorySeparator(program))
        {
            return IsExecutable(program) ? Path.GetFullPath(program) : null;
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in ExecutableExtensions(program))
            {
                string candidate = Path.Combine(directory, program + extension);
                if (IsExecutable(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static bool ContainsDirectorySeparator(string program) =>
        program.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        program.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static string[] ExecutableExtensions(string program)
    {
        if (!OperatingSystem.IsWindows() || Path.HasExtension(program))
        {
            return [string.Empty];
        }

        string pathExtensions = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM";
        return [string.Empty, .. pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries)];
    }

    private static bool IsExecutable(string candidate)
    {
        if (!File.Exists(candidate))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        UnixFileMode mode = File.GetUnixFileMode(candidate);
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
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
    ///     Drains both redirected streams without blocking a thread, waits for exit, and returns what was captured.
    /// </summary>
    /// <remarks>
    ///     Both reads are started before either is awaited, so neither pipe can fill while the other is being
    ///     consumed.
    /// </remarks>
    /// <param name="process">A started process with both output streams redirected.</param>
    /// <param name="cancellationToken">Cancels the wait for the child to exit.</param>
    /// <returns>The finished process's exit code and captured streams.</returns>
    public static async Task<ProcessResult> ReadToEndAsync(
        Process process,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);

        // Both reads are started before either is awaited, so neither pipe can fill while
        // the other is being consumed.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        string[] captured = await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);

        // Safe only now: both streams are at EOF, so the child cannot be blocked on write.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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

        // UseShellExecute is explicitly disabled above. The one caller that sets the
        // concatenated Arguments string is SquadHarnessLauncher.BuildStartInfo, for Windows
        // cmd/bat shims, and it refuses any argument containing a cmd metacharacter
        // (quotes, %, !, ^, &, |, <, >, ;, $, backtick, parentheses, CR, LF) before launch.
        using Process process = StartRetryingTextFileBusy(startInfo);

        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>ETXTBSY, the same value on Linux and macOS.</summary>
    private const int TextFileBusyErrorCode = 26;

    private const int TextFileBusyAttempts = 5;

    /// <summary>
    ///     The one place <see cref="ProcessRunner"/> and <see cref="StdioSession"/> start a child:
    ///     <see cref="Run"/>, <see cref="RunInteractive"/> and the session all start through it, retrying briefly
    ///     when exec fails with ETXTBSY on Linux or macOS. Up to five attempts, four retries 20, 40, 60 and 80 ms
    ///     apart, 200 ms in total.
    /// </summary>
    /// <remarks>
    ///     A file that was just written can still be open for writing in a child another thread forked in the
    ///     moment before its close-on-exec descriptors were closed, and exec refuses a file open for writing. The
    ///     window lasts only until that child execs, so a short retry starts the file once it is free: an
    ///     executable a test stub or an install has just written is not reported as one that cannot start. Any
    ///     other failure, ETXTBSY on the last attempt, and every failure on Windows (where 26 is a different Win32
    ///     error) surfaces unchanged.
    /// </remarks>
    /// <param name="startInfo">The start info, already made safe by the caller.</param>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    internal static Process StartRetryingTextFileBusy(ProcessStartInfo startInfo)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // Callers pass a start info with UseShellExecute off: Run and StdioSession through
                // CreateSafeStartInfo, RunInteractive by setting it before the call.
                return Process.Start(startInfo) // nosemgrep
                       ?? throw new InvalidOperationException("The child process could not be started.");
            }
            catch (Win32Exception ex) when (
                !OperatingSystem.IsWindows() && ex.NativeErrorCode == TextFileBusyErrorCode && attempt < TextFileBusyAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(20 * attempt));
            }
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

    private static async Task WaitForIoBoundedAsync(Task io)
    {
        // The killed tree may never reap. Give the pipe reads the same five-second bound as Run, on the real
        // clock like the reap bound in RunAsync's finally, so a caller's clock that stops advancing cannot
        // hold the timeout path open. A failure here is not surfaced: the path already reports a timeout.
        Task delay = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        _ = await Task.WhenAny(io, delay).ConfigureAwait(false);
    }

    private static void ReadCaptured(
        StreamReader reader,
        long? maxBytes,
        Action<string> store,
        Action<Exception> capture,
        Action complete)
    {
        try
        {
            store(ReadBounded(reader, maxBytes));
        }
        catch (IOException ex)
        {
            capture(ex);
        }
        catch (InvalidOperationException ex)
        {
            capture(ex);
        }
        catch (ArgumentException ex)
        {
            capture(ex);
        }
        catch (NotSupportedException ex)
        {
            capture(ex);
        }
        finally
        {
            complete();
        }
    }

    private static void WriteCaptured(
        StreamWriter writer,
        string input,
        Action<Exception> capture,
        Action complete)
    {
        try
        {
            WriteAndClose(writer, input);
        }
        catch (IOException ex)
        {
            capture(ex);
        }
        catch (InvalidOperationException ex)
        {
            capture(ex);
        }
        catch (ArgumentException ex)
        {
            capture(ex);
        }
        catch (NotSupportedException ex)
        {
            capture(ex);
        }
        finally
        {
            complete();
        }
    }

    private static string ReadBounded(StreamReader reader, long? maxBytes)
    {
        if (maxBytes is null)
            return reader.ReadToEnd();

        if (maxBytes.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes.Value, "maxBytes cannot be negative.");

        long max = maxBytes.Value;
        int bufferSize = (int)Math.Clamp(max > 16383 ? 16384 : max + 1, 1, 16384);
        char[] buffer = new char[bufferSize];
        StringBuilder sb = new();
        long totalBytes = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalBytes += reader.CurrentEncoding.GetByteCount(buffer, 0, read);
            if (totalBytes > max)
                throw new OutputLimitExceededException($"The process output exceeded the limit of {max.ToString(CultureInfo.InvariantCulture)} bytes.");

            sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private static void WriteAndClose(StreamWriter writer, string input)
    {
        try
        {
            writer.Write(input);
        }
        finally
        {
            // Close communicates EOF even for empty input. It also flushes any text still
            // buffered by StreamWriter, so failures during that final transfer propagate.
            writer.Close();
        }
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
