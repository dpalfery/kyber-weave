using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Contract test for how <c>scripts/proc-guard.sh run</c> handles a terminal on stdin.
/// </summary>
/// <remarks>
/// <para>
/// The wrapped command runs as a background job in its own process group. Under a controlling terminal
/// a read from that terminal would stop the job with SIGTTIN, and it would sit stopped until the
/// watchdog timeout fired. By default the guard therefore gives the command <c>/dev/null</c> as stdin
/// whenever its own stdin is a terminal. The <c>--tty</c> opt-out is not tested: it keeps the hang by design.
/// </para>
/// <para>
/// The pseudo-terminal comes from <c>script</c> (BSD form on macOS, util-linux form elsewhere). The test is skipped
/// when <c>script</c> is missing. The only child the guard starts is <c>sh -c 'read x; echo done'</c>, which exits by itself.
/// </para>
/// </remarks>
public sealed class ProcGuardTtyTests
{
    private static readonly TimeSpan HarnessTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MustFinishWithin = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A command that reads stdin under a pty must see EOF and exit 0 within seconds, not stop until the 20 s timeout (exit 124).
    /// </summary>
    [Fact]
    public void RunUnderPseudoTerminal_CommandReadingStdinSeesEofAndExits0()
    {
        string? scriptPath = FindOnPath("script");
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash") || scriptPath is null)
        {
            throw SkipException.ForSkip("A pseudo-terminal needs /bin/bash and the 'script' utility on PATH.");
        }

        string guard = Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "proc-guard.sh");
        string inner = "/bin/bash " + guard + " run --timeout 20 -- sh -c 'read x; echo done'";

        ProcessStartInfo startInfo = new(scriptPath)
        {
            WorkingDirectory = KyberWeaveTestPaths.ToolRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsMacOS())
        {
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("/dev/null");
            startInfo.ArgumentList.Add("/bin/bash");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(inner + "; echo guard-exit=$?");
        }
        else
        {
            startInfo.ArgumentList.Add("-qec");
            startInfo.ArgumentList.Add(inner + "; echo guard-exit=$?");
            startInfo.ArgumentList.Add("/dev/null");
        }

        Stopwatch clock = Stopwatch.StartNew();
        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, HarnessTimeout);
        clock.Stop();

        Assert.Contains("guard-exit=0", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("done", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < MustFinishWithin, $"The guarded read took {clock.Elapsed.TotalSeconds:F1} s; it should see EOF at once.");
    }

    private static string? FindOnPath(string executable)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
