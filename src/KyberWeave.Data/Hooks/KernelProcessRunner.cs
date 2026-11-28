using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Data.Hooks;

/// <summary>Runs programs through the kernel's deadlock-safe process runner and answers process and path questions.</summary>
internal sealed class KernelProcessRunner : IProcessRunner
{
    private const int MissingExecutableErrorCode = 2;

    /// <inheritdoc />
    public bool IsAlive(int processId) => ProcessRunner.IsAlive(processId);

    /// <inheritdoc />
    public string? FindOnPath(string program) => ProcessRunner.FindOnPath(program);

    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Program);

        if (request.WorkingDirectory is { Length: > 0 } workingDirectory && !Directory.Exists(workingDirectory))
        {
            throw WorkingDirectoryNotFound(workingDirectory);
        }

        ProcessStartInfo startInfo = new(request.Program)
        {
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is { } environment)
        {
            startInfo.Environment.Clear();
            foreach (KeyValuePair<string, string?> pair in environment)
            {
                if (pair.Value is not null)
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(
                startInfo,
                request.StandardInput ?? string.Empty,
                request.Timeout,
                request.MaxOutputBytes,
                time: null,
                cancellationToken).ConfigureAwait(false);

            return new ProcessRunResult(result.ExitCode, result.StandardOutput + result.StandardError, TimedOut: false)
            {
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError
            };
        }
        catch (Exception ex) when (IsMissingExecutable(ex))
        {
            throw MapMissingExecutable(ex, request.Program, request.WorkingDirectory);
        }
        catch (TimeoutException)
        {
            return new ProcessRunResult(-1, string.Empty, TimedOut: true) { StandardOutput = string.Empty, StandardError = string.Empty };
        }
    }

    private static bool IsMissingExecutable(Exception ex) =>
        ex is FileNotFoundException || (ex is Win32Exception win32 && win32.NativeErrorCode == MissingExecutableErrorCode);

    /// <summary>Names the cause of a failed start: a directory removed after the up-front check, or a missing program.</summary>
    /// <remarks>
    /// Linux reports a missing working directory with the same native error as a missing program, so the directory is
    /// checked again before blaming the command. A null or empty directory means "inherit the host's", so there is no
    /// directory to blame and the program is the only possible cause.
    /// </remarks>
    internal static Exception MapMissingExecutable(Exception ex, string program, string? workingDirectory)
    {
        if (ex is Win32Exception { NativeErrorCode: MissingExecutableErrorCode } &&
            !string.IsNullOrEmpty(workingDirectory) &&
            !Directory.Exists(workingDirectory))
        {
            return WorkingDirectoryNotFound(workingDirectory, ex);
        }

        return new FileNotFoundException($"command not found: {program}", program, ex);
    }

    private static DirectoryNotFoundException WorkingDirectoryNotFound(string workingDirectory, Exception? inner = null) =>
        new($"working directory not found: {workingDirectory}", inner);
}
