using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Full-duplex child process execution. A parent that fills stdin before draining output
/// can deadlock with a child that fills its output pipes before reading stdin.
/// </summary>
public sealed class ProcessRunnerInputTests
{
    private const int BytesPerStream = 300_000;
    private const int TimeoutSeconds = 30;

    private static string Emit(int bytes, char fill, bool toStandardError) =>
        $"head -c {bytes} /dev/zero | tr '\\0' '{fill}'{(toStandardError ? " >&2" : string.Empty)}";

    private static ProcessStartInfo CreateShellStartInfo(string script)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    [Fact]
    public async Task RunWithLargeInputAndOutputTransfersAllStreamsWithoutDeadlock()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell process execution tests are not run on Windows.");
        }

        string input = new string('i', BytesPerStream);
        ProcessStartInfo startInfo = CreateShellStartInfo(
            $"{Emit(BytesPerStream, 'e', toStandardError: true)}; " +
            $"{Emit(BytesPerStream, 'o', toStandardError: false)}; " +
            "count=$(wc -c | tr -d '[:space:]'); printf '\\nstdin:%s\\n' \"$count\"; exit 11");

        ProcessResult result = await Task
            .Run(() => ProcessRunner.Run(startInfo, input))
            .WaitAsync(TimeSpan.FromSeconds(TimeoutSeconds));

        Assert.Equal(new string('o', BytesPerStream) + $"\nstdin:{BytesPerStream}\n", result.StandardOutput);
        Assert.Equal(new string('e', BytesPerStream), result.StandardError);
        Assert.Equal(11, result.ExitCode);
    }

    [Fact]
    public void RunPreservesCallerEnvironmentOverrides()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell process execution tests are not run on Windows.");
        }

        ProcessStartInfo startInfo = CreateShellStartInfo("printf '%s' \"$KYBER_WEAVE_PROCESS_MARKER\"");
        startInfo.Environment["KYBER_WEAVE_PROCESS_MARKER"] = "from-caller";

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal("from-caller", result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("stdin")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public void RunWhenARequiredStreamIsNotRedirectedRejectsTheStartInfo(string stream)
    {
        ProcessStartInfo startInfo = CreateShellStartInfo("exit 0");
        switch (stream)
        {
            case "stdin":
                startInfo.RedirectStandardInput = false;
                break;
            case "stdout":
                startInfo.RedirectStandardOutput = false;
                break;
            case "stderr":
                startInfo.RedirectStandardError = false;
                break;
        }

        Assert.Throws<ArgumentException>(() => ProcessRunner.Run(startInfo, string.Empty));
    }

    [Fact]
    public void RunWhenArgumentsIsAConcatenatedStringRejectsTheStartInfo()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("sqlite3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Arguments = "--version"
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => ProcessRunner.Run(startInfo, string.Empty));
        Assert.Equal("startInfo", exception.ParamName);
    }

    [Fact]
    public void RunWhenProcessCannotStartPropagatesTheStartupFailure()
    {
        string missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"kyber-weave-missing-{Guid.NewGuid():N}");
        ProcessStartInfo startInfo = new ProcessStartInfo(missingExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        Assert.Throws<Win32Exception>(() => ProcessRunner.Run(startInfo, string.Empty));
    }

    [Fact]
    public async Task RunWhenChildClosesStdinPropagatesTheWriteFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell process execution tests are not run on Windows.");
        }

        ProcessStartInfo startInfo = CreateShellStartInfo("exec 0<&-; exit 0");
        string input = new string('i', BytesPerStream * 4);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await Task
                .Run(() => ProcessRunner.Run(startInfo, input))
                .WaitAsync(TimeSpan.FromSeconds(TimeoutSeconds)));
    }
}
