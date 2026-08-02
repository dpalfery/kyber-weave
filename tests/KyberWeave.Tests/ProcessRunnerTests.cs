using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pipe draining. The deadlock these guard against is invisible in normal use: every
/// fixture and every happy-path invocation is quiet enough to fit in the pipe buffer, so
/// the bug only appears against a real, talkative child process.
/// </summary>
public sealed class ProcessRunnerTests
{
    /// <summary>
    /// Comfortably beyond any OS pipe buffer (64 KB on Linux, less on macOS), on both
    /// streams — so the process deadlocks whichever stream a naive implementation reads
    /// first.
    /// </summary>
    private const int BytesPerStream = 300_000;

    private const int TimeoutSeconds = 30;

    /// <summary>
    /// Emits exactly <paramref name="bytes"/> of <paramref name="fill"/> on the given
    /// stream. Reads from <c>/dev/zero</c> rather than piping <c>yes</c> into <c>head</c>,
    /// because that arrangement leaves <c>yes</c> writing to a closed pipe and it reports
    /// "Broken pipe" on stderr — 25 stray bytes per invocation, which would make an exact
    /// length assertion wrong for reasons unrelated to draining.
    /// </summary>
    private static string Emit(int bytes, char fill, bool toStandardError) =>
        $"head -c {bytes} /dev/zero | tr '\\0' '{fill}'{(toStandardError ? " >&2" : string.Empty)}";

    /// <summary>
    /// Uses <c>/bin/sh</c>, so it does not run on Windows. The draining logic itself is
    /// platform-independent, and CI runs Linux.
    /// </summary>
    private static Process? StartTalkativeChild(string script)
    {
        if (OperatingSystem.IsWindows()) return null;

        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start /bin/sh.");
    }

    /// <summary>
    /// Runs the capture on a worker so a regression fails on a timeout rather than hanging
    /// the whole suite forever, which is what a deadlock would otherwise do to CI.
    /// </summary>
    private static ProcessResult CaptureWithinTimeout(Process process)
    {
        var capture = Task.Run(() => ProcessRunner.ReadToEnd(process));

        if (!capture.Wait(TimeSpan.FromSeconds(TimeoutSeconds)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }

            Assert.Fail(
                $"ProcessRunner.ReadToEnd did not return within {TimeoutSeconds}s. " +
                "Both pipes must be drained concurrently; reading one to the end before " +
                "the other, or waiting for exit before draining, deadlocks here.");
        }

        return capture.Result;
    }

    [Fact]
    public void DrainsBothStreamsWhenEachExceedsThePipeBuffer()
    {
        using var process = StartTalkativeChild(
            $"{Emit(BytesPerStream, 'e', toStandardError: true)}; " +
            $"{Emit(BytesPerStream, 'o', toStandardError: false)}; " +
            "exit 3");

        if (process is null) return; // Windows

        var result = CaptureWithinTimeout(process);

        // Full length, not merely non-empty: a truncated read would mean a pipe was
        // abandoned rather than drained.
        Assert.Equal(BytesPerStream, result.StandardOutput.Length);
        Assert.Equal(BytesPerStream, result.StandardError.Length);
        Assert.Equal(3, result.ExitCode);
    }

    /// <summary>
    /// The asymmetric case that hid the bug in `docs drift`: a large stdout with a quiet
    /// stderr looks fine, right up until the child has something to say on stderr.
    /// </summary>
    [Fact]
    public void DrainsALargeStdoutAlongsideASmallStderr()
    {
        using var process = StartTalkativeChild(
            $"{Emit(BytesPerStream, 'o', toStandardError: false)}; " +
            "echo warning >&2");

        if (process is null) return; // Windows

        var result = CaptureWithinTimeout(process);

        Assert.Equal(BytesPerStream, result.StandardOutput.Length);
        Assert.Equal("warning", result.StandardError.Trim());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void CapturesExitCodeAndOutputFromAQuietChild()
    {
        using var process = StartTalkativeChild("echo out; echo err >&2; exit 7");
        if (process is null) return; // Windows

        var result = CaptureWithinTimeout(process);

        Assert.Equal("out", result.StandardOutput.Trim());
        Assert.Equal("err", result.StandardError.Trim());
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public void RejectsANullProcess() =>
        Assert.Throws<ArgumentNullException>(() => ProcessRunner.ReadToEnd(null!));
}
