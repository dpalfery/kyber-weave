using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Data.Hooks;

/// <summary>Runs commands through the kernel's deadlock-safe process runner.</summary>
internal sealed class KernelProcessRunner : IProcessRunner
{
    private const long MaxOutputBytes = 4 * 1024 * 1024;
    private const int MissingExecutableErrorCode = 2;

    public Task<ProcessRunResult> RunAsync(
        IReadOnlyList<string> argv,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
        {
            throw new ArgumentException("A command needs at least a program name.", nameof(argv));
        }

        return Task.Run(() => Run(argv, workingDirectory, timeout, cancellationToken), cancellationToken);
    }

    private static ProcessRunResult Run(IReadOnlyList<string> argv, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"working directory not found: {workingDirectory}");
        }

        ProcessStartInfo startInfo = new(argv[0])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in argv.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, timeout, MaxOutputBytes, cancellationToken);
            return new ProcessRunResult(result.ExitCode, result.StandardOutput + result.StandardError, TimedOut: false);
        }
        catch (Exception ex) when (IsMissingExecutable(ex))
        {
            throw new FileNotFoundException($"command not found: {argv[0]}", argv[0], ex);
        }
        catch (TimeoutException)
        {
            return new ProcessRunResult(-1, string.Empty, TimedOut: true);
        }
        catch (OutputLimitExceededException ex)
        {
            return new ProcessRunResult(-1, ex.Message, TimedOut: false);
        }
    }

    private static bool IsMissingExecutable(Exception ex) =>
        ex is FileNotFoundException || (ex is Win32Exception win32 && win32.NativeErrorCode == MissingExecutableErrorCode);
}
