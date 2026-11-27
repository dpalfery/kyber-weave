using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;

using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Application.StatusLine;

/// <summary>Best-effort version probe for a harness binary.</summary>
public static class UtilitiesStatusLineHarnessProbe
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
