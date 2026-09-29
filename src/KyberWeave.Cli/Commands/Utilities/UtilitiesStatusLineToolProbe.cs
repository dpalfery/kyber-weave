using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>Runs a child process to completion, with a bounded wait.</summary>
/// <remarks>
///     A delegate rather than <c>IProcessExecutor</c> so the harness version probe can cap its wait:
///     a harness binary that never answers <c>--version</c> must not hang doctor. Production binds this
///     to <see cref="ProcessRunner.Run(ProcessStartInfo, string, TimeSpan?)" /> with a short timeout.
/// </remarks>
/// <param name="startInfo">The process to start, with all three streams redirected.</param>
/// <param name="standardInput">The complete standard input; empty for a version probe.</param>
internal delegate ProcessResult UtilitiesStatusLineProcessRunner(ProcessStartInfo startInfo, string standardInput);

/// <summary>Decides whether an executable is reachable on <c>PATH</c>.</summary>
/// <remarks>
///     Presence is answered by reading <c>PATH</c>, not by starting the tool: a prerequisite check must
///     have no side effect, and a tool that is present but slow or interactive must still count as
///     present. This slice is macOS and Linux only (D4), so there is no <c>PATHEXT</c> resolution to do.
/// </remarks>
internal sealed class UtilitiesStatusLineToolProbe
{
    private readonly Func<string, string?> _getEnvironmentVariable;

    /// <param name="getEnvironmentVariable">Reads a process environment variable; injected for tests.</param>
    public UtilitiesStatusLineToolProbe(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    /// <summary>Whether <paramref name="executable" /> names an existing file in a <c>PATH</c> directory.</summary>
    public bool IsOnPath(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        string? path = _getEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate) && UtilitiesStatusLineCommandComposition.HasExecutableBit(candidate)) return true;
        }

        return false;
    }
}

/// <summary>Best-effort version probe for a harness binary.</summary>
internal static class UtilitiesStatusLineHarnessProbe
{
    private const int MaximumVersionLength = 120;

    /// <summary>
    ///     The harness binary's reported version, or <see langword="null" /> when it cannot be read.
    /// </summary>
    /// <remarks>
    ///     A harness that is present but does not answer <c>--version</c>, or answers non-zero, is
    ///     reported as "version unknown" rather than as a failure: the version is informational and a
    ///     harness may not implement the flag. The wait is bounded by the injected runner, and a startup
    ///     exception is folded into "unknown" because the exception text can echo <c>PATH</c> values
    ///     doctor must not print.
    /// </remarks>
    public static string? TryVersion(UtilitiesStatusLineProcessRunner run, string executable)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--version");

        try
        {
            ProcessResult result = run(startInfo, string.Empty);
            return result.ExitCode == 0 ? FirstLine(result.StandardOutput) : null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static string? FirstLine(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed.Length <= MaximumVersionLength
                    ? trimmed
                    : trimmed[..MaximumVersionLength];
        }

        return null;
    }
}
