using System.ComponentModel;
using System.Diagnostics;

namespace KyberWeave.Cli.Update;

/// <summary>The adapter that starts a child on this console: the only code in the update that calls <c>Process.Start</c>.</summary>
internal sealed class ProcessConsoleLauncher : IConsoleProcessLauncher
{
    /// <inheritdoc />
    public int Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, bool discardOutput = false)
    {
        ProcessStartInfo child = new(fileName) { UseShellExecute = false };
        if (discardOutput)
        {
            child.RedirectStandardOutput = true;
            child.RedirectStandardError = true;
        }

        foreach (string argument in arguments)
            child.ArgumentList.Add(argument);

        using Process? started = Process.Start(child);
        if (started is null)
            throw new InvalidOperationException($"the process could not be started: {fileName}");

        // Redirected output must be drained, or a child that writes a lot blocks on a full pipe. With no handler attached,
        // the asynchronous reads discard what they read.
        if (discardOutput)
        {
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
        }

        if (started.WaitForExit(timeout))
            return started.ExitCode;

        try
        {
            if (!started.HasExited)
                started.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            // It exited between the check and the kill, or the tree kill itself failed (Process.Kill(true)
            // aggregates the per-process results into one exception when any member refuses to die). The
            // timeout below still reports the step.
        }

        started.WaitForExit(TimeSpan.FromSeconds(5));
        throw new TimeoutException(
            $"The process did not exit within {(int)timeout.TotalSeconds} seconds, and its process tree was stopped.");
    }
}
